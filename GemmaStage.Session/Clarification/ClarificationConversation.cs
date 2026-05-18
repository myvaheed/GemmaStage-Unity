using GemmaStage.Session.Inquirer;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Resilience;
using GemmaStage.Session.Stores;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace GemmaStage.Session.Clarification;

public sealed record ClarificationTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    ClarificationParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson);

// The Clarifier owns two distinct stages - Revise and Resolve - and each
// stage gets its own system prompt and tools schema, the same way
// MainIdeaComparator splits Decompose and Compare. Two per-stage
// ConversationConfig handles are created up front; each per-call
// ConversationCreate inherits the right system prompt and constrained-decoding
// schema. Per-call llama_context (no shared KV across stages or calls).
public sealed class ClarificationConversation : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _reviseConfig;
    private readonly ConversationConfigHandle _resolveConfig;
    private readonly Action<string>? _warn;

    private long _turnSequence;
    private bool _disposed;

    public ClarificationConversation(
        EngineHandle engine,
        Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _warn = warn;

        _reviseConfig = GemmaStageNative.ConversationConfigCreate(
            engine,
            ClarificationRevisePrompts.System,
            ClarificationRevisePrompts.ToolsJson,
            enableConstrainedDecoding: true);
        GemmaStageNative.ConversationConfigSetEnableThinking(_reviseConfig, ClarificationRevisePrompts.EnableThinking);

        _resolveConfig = GemmaStageNative.ConversationConfigCreate(
            engine,
            ClarificationResolvePrompts.System,
            ClarificationResolvePrompts.ToolsJson,
            enableConstrainedDecoding: true);
        GemmaStageNative.ConversationConfigSetEnableThinking(_resolveConfig, ClarificationResolvePrompts.EnableThinking);
    }

    public ClarificationTurnResult Revise(IReadOnlyList<ArchivedConcernEntry> concerns)
    {
        Guard.NotNull(concerns);
        ThrowIfDisposed();

        var prompt = BuildRevisePrompt(concerns);
        return RunStage(_reviseConfig, ClarificationRevisePrompts.EnableThinking, prompt, expectRevise: true, stageLabel: "revise", role: "Clarification.Revise");
    }

    public ClarificationTurnResult Resolve(
        string transcriptChunk,
        IReadOnlyList<RevisedQuestion> remainingConcerns)
    {
        Guard.NotNullOrWhiteSpace(transcriptChunk);
        Guard.NotNull(remainingConcerns);
        ThrowIfDisposed();

        var prompt = BuildResolvePrompt(transcriptChunk, remainingConcerns);
        return RunStage(_resolveConfig, ClarificationResolvePrompts.EnableThinking, prompt, expectRevise: false, stageLabel: "resolve", role: "Clarification.Resolve");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _resolveConfig.Dispose();
        _reviseConfig.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private ClarificationTurnResult RunStage(
        ConversationConfigHandle config,
        bool initialThinking,
        string prompt,
        bool expectRevise,
        string stageLabel,
        string role)
    {
        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parse = ToolCallRetry.Execute(role, config, initialThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, config);
            using var response = GemmaStageNative.ConversationSendText(conversation, prompt);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return ClarificationResponseParser.Parse(responseJson);
        }, Warn);

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);

        if (expectRevise && !parse.IsRevise)
        {
            Warn($"Clarification {stageLabel} turn {turn} produced \"{parse.ToolName}\" instead of revise.");
        }
        else if (!expectRevise && !parse.IsResolve)
        {
            Warn($"Clarification {stageLabel} turn {turn} produced \"{parse.ToolName}\" instead of resolve.");
        }

        return new ClarificationTurnResult(turn, timestamp, parse, benchmark, responseJson);
    }

    private static string BuildRevisePrompt(IReadOnlyList<ArchivedConcernEntry> concerns)
    {
        return ClarificationRevisePrompts.UserTemplate
            .Replace("{archived_concerns_list}", BuildConcernsList(concerns))
            .TrimEnd();
    }

    private static string BuildResolvePrompt(
        string transcriptChunk,
        IReadOnlyList<RevisedQuestion> concerns)
    {
        return ClarificationResolvePrompts.UserTemplate
            .Replace("{chunk_text}", transcriptChunk.TrimEnd())
            .Replace("{remaining_questions_list}", BuildRevisedConcernsList(concerns))
            .TrimEnd();
    }

    private static string BuildConcernsList(IReadOnlyList<ArchivedConcernEntry> concerns)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < concerns.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            var c = concerns[i];
            sb.Append('[').Append(c.Id).Append("] (").Append(TypeWireString(c.Type)).Append(") ").Append(c.Question);
        }
        return sb.ToString();
    }

    private static string BuildRevisedConcernsList(IReadOnlyList<RevisedQuestion> concerns)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < concerns.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            var c = concerns[i];
            sb.Append('[').Append(c.Id).Append("] (").Append(TypeWireString(c.Type)).Append(") ").Append(c.Question);
        }
        return sb.ToString();
    }

    private static string TypeWireString(InquirerConcernType type) => type switch
    {
        InquirerConcernType.TopicUnknown => ClarificationSchemas.ConcernTypeTopicUnknown,
        InquirerConcernType.ComprehensionGap => ClarificationSchemas.ConcernTypeComprehensionGap,
        InquirerConcernType.DetailRequest => ClarificationSchemas.ConcernTypeDetailRequest,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown concern type."),
    };

    private static BenchmarkSnapshot ReadBenchmark(ConversationHandle conversation)
    {
        try
        {
            using var info = GemmaStageNative.ConversationGetBenchmarkInfo(conversation);
            return GemmaStageNative.GetBenchmarkSnapshot(info, conversation);
        }
        catch
        {
            return BenchmarkSnapshot.Empty;
        }
    }

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }

    private void ThrowIfDisposed()
    {
        Guard.NotDisposed(_disposed, this);
    }
}
