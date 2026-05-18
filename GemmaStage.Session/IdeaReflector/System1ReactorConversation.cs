using System.Text;
using System.Threading;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Resilience;
using GemmaStage.Session.Stores;

namespace GemmaStage.Session.IdeaReflector;

// Cheap retelling-only sub-role. No thinking. Sees current main-idea, last 4
// retellings, and this cycle's raw transcript.
public sealed class System1ReactorConversation : IDisposable
{
    private const int RecentRetellingsLimit = 4;

    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;

    private long _turnSequence;
    private bool _disposed;

    public System1ReactorConversation(EngineHandle engine, Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _warn = warn;

        _config = GemmaStageNative.ConversationConfigCreate(
            engine,
            System1ReactorPrompts.System,
            System1ReactorPrompts.ToolsJson,
            enableConstrainedDecoding: true);
        GemmaStageNative.ConversationConfigSetEnableThinking(_config, System1ReactorPrompts.EnableThinking);
    }

    public System1ReactorTurnResult React(
        string currentMainIdea,
        IReadOnlyList<string> previousRetellings,
        IReadOnlyList<RawContentSegment> rawContent)
    {
        Guard.NotNull(currentMainIdea);
        Guard.NotNull(previousRetellings);
        Guard.NotNull(rawContent);
        ThrowIfDisposed();

        var prompt = BuildPrompt(currentMainIdea, previousRetellings, rawContent);

        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parse = ToolCallRetry.Execute("System1Reactor", _config, System1ReactorPrompts.EnableThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendText(conversation, prompt);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return System1ReactorResponseParser.Parse(responseJson);
        }, Warn);

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);

        if (parse.IsFailure)
        {
            Warn($"System1Reactor turn {turn} failed: {parse.Error}");
        }

        return new System1ReactorTurnResult(turn, timestamp, parse, benchmark, responseJson);
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

    internal static string BuildPrompt(
        string currentMainIdea,
        IReadOnlyList<string> previousRetellings,
        IReadOnlyList<RawContentSegment> rawContent)
    {
        return System1ReactorPrompts.UserTemplate
            .Replace("{current_main_idea}", string.IsNullOrWhiteSpace(currentMainIdea) ? "  (none yet)" : currentMainIdea)
            .Replace("{recent_retellings}", BuildRetellingsBlock(previousRetellings, RecentRetellingsLimit))
            .Replace("{raw_content}", BuildRawContentText(rawContent))
            .TrimEnd();
    }

    private static string BuildRetellingsBlock(IReadOnlyList<string> retellings, int limit)
    {
        if (retellings.Count == 0)
        {
            return "  (none yet)";
        }

        var startIdx = retellings.Count > limit ? retellings.Count - limit : 0;
        var sb = new StringBuilder();
        for (int i = startIdx; i < retellings.Count; i++)
        {
            if (i > startIdx) sb.AppendLine();
            sb.Append("  - ").Append(retellings[i]);
        }
        return sb.ToString();
    }

    private static string BuildRawContentText(IReadOnlyList<RawContentSegment> rawContent)
    {
        if (rawContent.Count == 0)
        {
            return "  (no new content)";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < rawContent.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            var segment = rawContent[i];
            var label = segment.Kind switch
            {
                RawContentKind.Image => "Slide",
                RawContentKind.QuestionMarker => "Question",
                RawContentKind.QaClosedMarker => "Closed Q&A",
                _ => "Audio",
            };
            sb.Append("  [").Append(label).Append("] ").Append(segment.Text.Trim());
        }
        return sb.ToString();
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
