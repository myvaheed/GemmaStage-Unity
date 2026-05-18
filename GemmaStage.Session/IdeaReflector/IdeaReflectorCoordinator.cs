using GemmaStage.Session.Native;
using GemmaStage.Session.Resilience;
using GemmaStage.Session.Stores;

namespace GemmaStage.Session.IdeaReflector;

// Owns the per-cycle System1 → System2 chain plus end-of-live IdeaComprehension
// hook. Replaces the previous IdeaReflectorConversation.
public sealed class IdeaReflectorCoordinator : IDisposable
{
    private readonly System1ReactorConversation _system1;
    private readonly System2ReflectorConversation _system2;
    private readonly Action<string>? _warn;
    private readonly IdeaReflectorStateTracker _state;
    private bool _disposed;

    public IdeaReflectorCoordinator(
        EngineHandle engine,
        string? topic = null,
        Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _warn = warn;
        _state = new IdeaReflectorStateTracker(topic);

        System1ReactorConversation? s1 = null;
        try
        {
            s1 = new System1ReactorConversation(engine, warn);
            _system2 = new System2ReflectorConversation(engine, warn);
            _system1 = s1;
        }
        catch
        {
            s1?.Dispose();
            throw;
        }
    }

    public string? Topic => _state.Topic;

    public string Thesis => _state.Thesis;

    public string MainIdeaText => _state.MainIdeaText;

    public string MainIdeaUnderstanding => _state.MainIdeaUnderstanding;

    public AudienceSideRecall AudienceRecall => _state.AudienceRecall;

    public IdeaReflectorTurnResult Reflect(
        IReadOnlyList<RawContentSegment> rawContent,
        IReadOnlyList<string> previousRetellings)
    {
        Guard.NotNull(rawContent);
        Guard.NotNull(previousRetellings);
        ThrowIfDisposed();

        var s1Turn = _system1.React(_state.MainIdeaUnderstanding, previousRetellings, rawContent);

        string? retelling = null;
        if (s1Turn.Parse.Output is { } s1Output)
        {
            // System1 carries the topic across cycles. "unknown" leaves the
            // current topic untouched.
            if (!string.Equals(s1Output.Topic, IdeaReflectorSchemas.UnknownTopicValue, StringComparison.OrdinalIgnoreCase))
            {
                _state.SetTopic(s1Output.Topic);
            }
            retelling = s1Output.Retelling;
        }

        System2ReflectorTurnResult? s2Turn = null;

        if (retelling is not null)
        {
            s2Turn = _system2.Reflect(_state.Topic, _state.MainIdeaText, retelling);

            if (s2Turn.Parse.Output is { } s2Output)
            {
                if (!string.Equals(s2Output.Topic, IdeaReflectorSchemas.UnknownTopicValue, StringComparison.OrdinalIgnoreCase))
                {
                    _state.SetTopic(s2Output.Topic);
                }
                _state.SetMainIdeaUnderstanding(s2Output.MainIdeaUnderstanding);
            }
        }

        var combinedError = s1Turn.Parse.Error ?? s2Turn?.Parse.Error;

        IdeaReflectorReflection? reflection = null;
        if (retelling is not null)
        {
            reflection = new IdeaReflectorReflection(
                Topic: _state.Topic ?? IdeaReflectorSchemas.UnknownTopicValue,
                Retelling: retelling,
                MainIdeaUnderstanding: _state.MainIdeaText);
        }

        var combinedParse = new IdeaReflectorParseResult(
            ToolName: combinedError is null ? "System1Reactor+System2Reflector" : null,
            Reflection: reflection,
            Error: combinedError,
            RawAssistantContent: null);

        var rawJson = ComposeRawJson(s1Turn.RawResponseJson, s2Turn?.RawResponseJson);

        return new IdeaReflectorTurnResult(
            TurnSequence: s1Turn.TurnSequence,
            Timestamp: s1Turn.Timestamp,
            Parse: combinedParse,
            Benchmark: s1Turn.Benchmark,
            RawResponseJson: rawJson,
            TopicAfter: _state.Topic,
            MainIdeaUnderstandingAfter: _state.MainIdeaUnderstanding,
            AudienceRecallAfter: _state.AudienceRecall,
            System1Turn: s1Turn,
            System2Turn: s2Turn);
    }

    // End-of-live thesis hook. IdeaComprehension reads the final
    // main_idea_understanding and emits a single thesis sentence.
    public void ApplyThesis(string thesis)
    {
        ThrowIfDisposed();
        _state.SetThesis(thesis);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _system2.Dispose();
        _system1.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static string? ComposeRawJson(string? s1Json, string? s2Json)
    {
        if (s1Json is null && s2Json is null) return null;
        if (s2Json is null) return s1Json;
        if (s1Json is null) return s2Json;
        return $"{{\"system1\":{s1Json},\"system2\":{s2Json}}}";
    }

    private void ThrowIfDisposed() => Guard.NotDisposed(_disposed, this);
}
