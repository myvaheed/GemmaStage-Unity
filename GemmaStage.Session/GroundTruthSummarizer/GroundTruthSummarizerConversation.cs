using System.Collections.Generic;
using System.Text;
using System.Threading;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.GroundTruthSummarizer;

public sealed record GroundTruthSummarizerTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    GroundTruthSummarizerParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson);

// MAP-or-REDUCE conversation. MAP appends new claims per chunk (with the prior
// accumulated claim list as context); REDUCE deduplicates and emits the final
// thesis + flat claim list.
public sealed class GroundTruthSummarizerConversation : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;
    private readonly string _role;
    private readonly bool _enableThinking;

    private long _turnSequence;
    private bool _disposed;

    private GroundTruthSummarizerConversation(
        EngineHandle engine,
        ConversationConfigHandle config,
        Action<string>? warn,
        string role,
        bool enableThinking)
    {
        _engine = engine;
        _config = config;
        _warn = warn;
        _role = role;
        _enableThinking = enableThinking;
    }

    public static GroundTruthSummarizerConversation CreateMap(
        EngineHandle engine,
        Action<string>? warn = null)
    {
        return Create(
            engine,
            GroundTruthSummarizerMapPrompts.System,
            GroundTruthSummarizerMapPrompts.EnableThinking,
            GroundTruthSummarizerMapPrompts.ToolsJson,
            warn,
            "GroundTruthSummarizer.Map");
    }

    public static GroundTruthSummarizerConversation CreateReduce(
        EngineHandle engine,
        Action<string>? warn = null)
    {
        return Create(
            engine,
            GroundTruthSummarizerReducePrompts.System,
            GroundTruthSummarizerReducePrompts.EnableThinking,
            GroundTruthSummarizerReducePrompts.ToolsJson,
            warn,
            "GroundTruthSummarizer.Reduce");
    }

    public GroundTruthSummarizerTurnResult SendChunk(
        string chunkText,
        int chunkIndex,
        int totalChunks,
        IReadOnlyList<string> existingClaims)
    {
        Guard.NotNullOrWhiteSpace(chunkText);
        Guard.NotNull(existingClaims);
        if (chunkIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, "Chunk index is 1-based.");
        }
        if (totalChunks < chunkIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(totalChunks), totalChunks, "Total chunks must cover the current index.");
        }
        ThrowIfDisposed();

        var prompt = BuildMapPrompt(chunkText, chunkIndex, totalChunks, existingClaims);
        return Run(prompt);
    }

    public GroundTruthSummarizerTurnResult SendReduce(IReadOnlyList<string> accumulatedClaims)
    {
        Guard.NotNull(accumulatedClaims);
        ThrowIfDisposed();

        var prompt = BuildReducePrompt(accumulatedClaims);
        return Run(prompt);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _config.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    internal static string BuildMapPrompt(
        string chunkText,
        int chunkIndex,
        int totalChunks,
        IReadOnlyList<string> existingClaims)
    {
        var header = totalChunks == 1
            ? "Ground-truth document (single chunk):"
            : $"Ground-truth document chunk {chunkIndex} of {totalChunks}:";

        return GroundTruthSummarizerMapPrompts.UserTemplate
            .Replace("{chunk_header}", header)
            .Replace("{existing_claims}", BuildClaimsBlock(existingClaims, "(none yet — every claim you emit will be new)"))
            .Replace("{chunk_text}", chunkText.TrimEnd())
            .TrimEnd();
    }

    internal static string BuildReducePrompt(IReadOnlyList<string> accumulatedClaims)
    {
        return GroundTruthSummarizerReducePrompts.UserTemplate
            .Replace("{accumulated_claims}", BuildClaimsBlock(accumulatedClaims, "(none)"))
            .Replace("{reduce_tool_name}", GroundTruthSummarizerSchemas.ReduceToolName)
            .TrimEnd();
    }

    private static string BuildClaimsBlock(IReadOnlyList<string> claims, string emptyPlaceholder)
    {
        if (claims.Count == 0)
        {
            return "  " + emptyPlaceholder;
        }
        var sb = new StringBuilder();
        for (int i = 0; i < claims.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append("  - ").Append(claims[i]);
        }
        return sb.ToString();
    }

    private GroundTruthSummarizerTurnResult Run(string prompt)
    {
        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parse = ToolCallRetry.Execute(_role, _config, _enableThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendText(conversation, prompt);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return GroundTruthSummarizerResponseParser.Parse(responseJson);
        }, Warn);

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);

        if (parse.IsFailure)
        {
            Warn($"{_role} turn {turn} failed: {parse.Error}");
        }

        return new GroundTruthSummarizerTurnResult(turn, timestamp, parse, benchmark, responseJson);
    }

    private static GroundTruthSummarizerConversation Create(
        EngineHandle engine,
        string systemPrompt,
        bool enableThinking,
        string toolsJson,
        Action<string>? warn,
        string role)
    {
        Guard.NotNull(engine);

        ConversationConfigHandle? config = null;
        try
        {
            config = GemmaStageNative.ConversationConfigCreate(
                engine,
                systemPrompt,
                toolsJson,
                enableConstrainedDecoding: true);
            GemmaStageNative.ConversationConfigSetEnableThinking(config, enableThinking);
        }
        catch
        {
            config?.Dispose();
            throw;
        }

        return new GroundTruthSummarizerConversation(engine, config, warn, role, enableThinking);
    }

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

    private void Warn(string message) => _warn?.Invoke(message);

    private void ThrowIfDisposed() => Guard.NotDisposed(_disposed, this);
}
