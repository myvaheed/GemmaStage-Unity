using System.Threading;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.IdeaReflector;

// One-shot post-live thesis inference. Inputs: topic + final
// main_idea_understanding paragraph. Output: a single short thesis sentence.
public sealed class IdeaComprehensionConversation : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;

    private long _turnSequence;
    private bool _disposed;

    public IdeaComprehensionConversation(EngineHandle engine, Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _warn = warn;

        _config = GemmaStageNative.ConversationConfigCreate(
            engine,
            IdeaComprehensionPrompts.System,
            IdeaComprehensionPrompts.ToolsJson,
            enableConstrainedDecoding: true);
        GemmaStageNative.ConversationConfigSetEnableThinking(_config, IdeaComprehensionPrompts.EnableThinking);
    }

    public IdeaComprehensionTurnResult Infer(string? topic, string mainIdeaUnderstanding)
    {
        Guard.NotNull(mainIdeaUnderstanding);
        ThrowIfDisposed();

        var prompt = BuildPrompt(topic, mainIdeaUnderstanding);

        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parse = ToolCallRetry.Execute("IdeaComprehension", _config, IdeaComprehensionPrompts.EnableThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendText(conversation, prompt);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return IdeaComprehensionResponseParser.Parse(responseJson);
        }, Warn);

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);

        if (parse.IsFailure)
        {
            Warn($"IdeaComprehension turn {turn} failed: {parse.Error}");
        }

        return new IdeaComprehensionTurnResult(turn, timestamp, parse, benchmark, responseJson);
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

    internal static string BuildPrompt(string? topic, string mainIdeaUnderstanding)
    {
        return IdeaComprehensionPrompts.UserTemplate
            .Replace("{topic}", string.IsNullOrWhiteSpace(topic) ? IdeaReflectorSchemas.UnknownTopicValue : topic)
            .Replace("{main_idea_understanding}", string.IsNullOrEmpty(mainIdeaUnderstanding) ? "(empty)" : mainIdeaUnderstanding)
            .TrimEnd();
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
