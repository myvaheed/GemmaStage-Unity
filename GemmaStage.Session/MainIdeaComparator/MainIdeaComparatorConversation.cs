using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Resilience;
using System;
using System.Threading;

namespace GemmaStage.Session.MainIdeaComparator;

public sealed record MainIdeaComparatorTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    MainIdeaComparatorParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson);

public sealed class MainIdeaComparatorConversation : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;
    private readonly string _role;
    private readonly bool _initialThinking;
    private readonly ConversationKind _kind;

    private long _turnSequence;
    private bool _disposed;

    private enum ConversationKind { Coverage, Thesis }

    private MainIdeaComparatorConversation(
        EngineHandle engine,
        ConversationConfigHandle config,
        Action<string>? warn,
        string role,
        bool initialThinking,
        ConversationKind kind)
    {
        _engine = engine;
        _config = config;
        _warn = warn;
        _role = role;
        _initialThinking = initialThinking;
        _kind = kind;
    }

    public static MainIdeaComparatorConversation CreateCoverage(
        EngineHandle engine,
        Action<string>? warn = null)
    {
        return Create(
            engine,
            MainIdeaComparatorCoveragePrompts.System,
            MainIdeaComparatorCoveragePrompts.EnableThinking,
            MainIdeaComparatorCoveragePrompts.ToolsJson,
            warn,
            "MainIdeaComparator.Coverage",
            ConversationKind.Coverage);
    }

    public static MainIdeaComparatorConversation CreateThesis(
        EngineHandle engine,
        Action<string>? warn = null)
    {
        return Create(
            engine,
            MainIdeaComparatorThesisPrompts.System,
            MainIdeaComparatorThesisPrompts.EnableThinking,
            MainIdeaComparatorThesisPrompts.ToolsJson,
            warn,
            "MainIdeaComparator.Thesis",
            ConversationKind.Thesis);
    }

    // One coverage call per GT claim. The audience-side input is the cumulative
    // main_idea_understanding paragraph.
    public MainIdeaComparatorTurnResult SendCoverage(string anchorClaim, string audienceMainIdea)
    {
        Guard.NotNullOrWhiteSpace(anchorClaim);
        Guard.NotNullOrWhiteSpace(audienceMainIdea);
        ThrowIfDisposed();
        ExpectKind(ConversationKind.Coverage);

        var prompt = BuildCoveragePrompt(anchorClaim, audienceMainIdea);
        return RunCall(prompt);
    }

    public MainIdeaComparatorTurnResult SendThesis(string anchorThesis, string audienceThesis)
    {
        Guard.NotNullOrWhiteSpace(anchorThesis);
        Guard.NotNullOrWhiteSpace(audienceThesis);
        ThrowIfDisposed();
        ExpectKind(ConversationKind.Thesis);

        var prompt = BuildThesisPrompt(anchorThesis, audienceThesis);
        return RunCall(prompt);
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

    internal static string BuildCoveragePrompt(string anchorClaim, string audienceMainIdea)
    {
        return MainIdeaComparatorCoveragePrompts.UserTemplate
            .Replace("{anchor_claim}", anchorClaim.Trim())
            .Replace("{audience_main_idea}", audienceMainIdea.Trim())
            .Replace("{coverage_tool_name}", MainIdeaComparatorSchemas.CoverageToolName)
            .TrimEnd();
    }

    internal static string BuildThesisPrompt(string anchorThesis, string audienceThesis)
    {
        return MainIdeaComparatorThesisPrompts.UserTemplate
            .Replace("{anchor_thesis}", anchorThesis.Trim())
            .Replace("{audience_thesis}", audienceThesis.Trim())
            .Replace("{thesis_tool_name}", MainIdeaComparatorSchemas.ThesisToolName)
            .TrimEnd();
    }

    private MainIdeaComparatorTurnResult RunCall(string prompt)
    {
        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parse = ToolCallRetry.Execute(_role, _config, _initialThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendText(conversation, prompt);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return MainIdeaComparatorResponseParser.Parse(responseJson);
        }, Warn);

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);

        var expectedOk = _kind switch
        {
            ConversationKind.Coverage => parse.IsCoverage,
            ConversationKind.Thesis => parse.IsThesis,
            _ => false,
        };

        if (!expectedOk)
        {
            Warn($"MainIdeaComparator {_kind} turn {turn} produced \"{parse.ToolName}\" instead of {_kind.ToString().ToLowerInvariant()}.");
        }

        return new MainIdeaComparatorTurnResult(turn, timestamp, parse, benchmark, responseJson);
    }

    private void ExpectKind(ConversationKind kind)
    {
        if (_kind != kind)
        {
            throw new InvalidOperationException(
                $"This conversation is configured for {_kind}; cannot send {kind} prompt.");
        }
    }

    private static MainIdeaComparatorConversation Create(
        EngineHandle engine,
        string systemPrompt,
        bool enableThinking,
        string toolsJson,
        Action<string>? warn,
        string role,
        ConversationKind kind)
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

        return new MainIdeaComparatorConversation(engine, config, warn, role, enableThinking, kind);
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

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }

    private void ThrowIfDisposed()
    {
        Guard.NotDisposed(_disposed, this);
    }
}
