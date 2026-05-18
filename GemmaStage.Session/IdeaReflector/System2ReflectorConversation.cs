using System.Threading;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.IdeaReflector;

// Cumulative main_idea_understanding builder. Thinking enabled. Sees the prior
// topic, the prior main_idea_understanding, and the latest System1 retelling.
// Emits a refreshed topic + an enriched plain-text main_idea_understanding.
public sealed class System2ReflectorConversation : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;

    private long _turnSequence;
    private bool _disposed;

    public System2ReflectorConversation(EngineHandle engine, Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _warn = warn;

        _config = GemmaStageNative.ConversationConfigCreate(
            engine,
            System2ReflectorPrompts.System,
            System2ReflectorPrompts.ToolsJson,
            enableConstrainedDecoding: true);
        GemmaStageNative.ConversationConfigSetEnableThinking(_config, System2ReflectorPrompts.EnableThinking);
    }

    public System2ReflectorTurnResult Reflect(
        string? priorTopic,
        string priorMainIdea,
        string latestRetelling)
    {
        Guard.NotNull(priorMainIdea);
        Guard.NotNullOrWhiteSpace(latestRetelling);
        ThrowIfDisposed();

        var prompt = BuildPrompt(priorTopic, priorMainIdea, latestRetelling);

        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parse = ToolCallRetry.Execute("System2Reflector", _config, System2ReflectorPrompts.EnableThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendText(conversation, prompt);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return System2ReflectorResponseParser.Parse(responseJson);
        }, Warn);

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);

        if (parse.IsFailure)
        {
            Warn($"System2Reflector turn {turn} failed: {parse.Error}");
        }

        return new System2ReflectorTurnResult(turn, timestamp, parse, benchmark, responseJson);
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

    internal static string BuildPrompt(string? priorTopic, string priorMainIdea, string latestRetelling)
    {
        return System2ReflectorPrompts.UserTemplate
            .Replace("{topic}", string.IsNullOrWhiteSpace(priorTopic) ? IdeaReflectorSchemas.UnknownTopicValue : priorTopic)
            .Replace("{prior_main_idea}", string.IsNullOrEmpty(priorMainIdea) ? "(empty)" : priorMainIdea)
            .Replace("{latest_retelling}", latestRetelling)
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
