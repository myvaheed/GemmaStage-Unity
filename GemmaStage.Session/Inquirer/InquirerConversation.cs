using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using System.Threading;

namespace GemmaStage.Session.Inquirer;

public sealed class InquirerConversation : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;
    private readonly InquirerStateTracker _stateTracker;

    private long _turnSequence;
    private bool _disposed;

    public InquirerConversation(
        EngineHandle engine,
        IEnumerable<InquirerConcern>? openConcerns = null,
        int concernCapacity = InquirerSchemas.ConcernMaxItems,
        Action<string>? warn = null)
    {
        Guard.NotNull(engine);

        _engine = engine;
        _warn = warn;
        _stateTracker = new InquirerStateTracker(openConcerns, concernCapacity);

        _config = GemmaStageNative.ConversationConfigCreate(
            engine,
            InquirerPrompts.System,
            InquirerPrompts.ToolsJson,
            enableConstrainedDecoding: true);
        GemmaStageNative.ConversationConfigSetEnableThinking(_config, InquirerPrompts.EnableThinking);
    }

    public InquirerStateSnapshot SnapshotState(string? topic, string? mainIdeaUnderstanding)
    {
        ThrowIfDisposed();
        return _stateTracker.Snapshot(topic, mainIdeaUnderstanding);
    }

    internal IReadOnlyList<ArchivedConcern> ArchiveOpenConcerns()
    {
        ThrowIfDisposed();
        return _stateTracker.ArchiveOpenConcerns();
    }

    internal void LoadConcernsForFinalQA(IEnumerable<InquirerConcern> concerns)
    {
        ThrowIfDisposed();
        _stateTracker.LoadOpenConcerns(concerns);
    }

    public InquirerTurnResult Reflect(
        string? topic,
        string? mainIdeaUnderstanding,
        IReadOnlyList<string> retellings,
        bool restricted = false)
    {
        Guard.NotNull(retellings);
        ThrowIfDisposed();

        var stateBeforeTurn = _stateTracker.Snapshot(topic, mainIdeaUnderstanding);
        var turn = _stateTracker.PrepareTurn(topic, mainIdeaUnderstanding, retellings, restricted: restricted);

        using var slot = _engine.AcquireConversationSlot();
        using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);

        using var response = GemmaStageNative.ConversationSendText(conversation, turn.Prompt);
        var responseJson = GemmaStageNative.JsonResponseGetString(response);
        var benchmark = ReadBenchmark(conversation);
        var timestamp = DateTimeOffset.UtcNow;
        var turnNumber = Interlocked.Increment(ref _turnSequence);

        var parse = InquirerResponseParser.Parse(responseJson);
        if (parse.IsFailure || parse.Reflection is null)
        {
            var error = parse.Error ?? "Unknown Inquirer parse failure.";
            Warn($"Inquirer reflection turn {turnNumber} failed: {error}");
            return new InquirerTurnResult(
                turnNumber,
                timestamp,
                parse,
                benchmark,
                responseJson,
                stateBeforeTurn,
                Array.Empty<ArchivedConcern>());
        }

        if (parse.ToolName != InquirerSchemas.ReflectionToolName)
        {
            var error = $"Inquirer reflection turn {turnNumber} produced unexpected tool \"{parse.ToolName}\".";
            Warn(error);
            return new InquirerTurnResult(
                turnNumber,
                timestamp,
                parse with { Reflection = null, Error = error },
                benchmark,
                responseJson,
                stateBeforeTurn,
                Array.Empty<ArchivedConcern>());
        }

        try
        {
            var apply = _stateTracker.ApplyReflection(parse.Reflection, turn, topic, mainIdeaUnderstanding, Warn);
            return new InquirerTurnResult(
                turnNumber,
                timestamp,
                parse,
                benchmark,
                responseJson,
                apply.State,
                apply.ArchivedConcerns);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Warn($"Inquirer reflection turn {turnNumber} failed state validation: {ex.Message}");
            return new InquirerTurnResult(
                turnNumber,
                timestamp,
                parse with { Reflection = null, Error = ex.Message },
                benchmark,
                responseJson,
                stateBeforeTurn,
                Array.Empty<ArchivedConcern>());
        }
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
