using GemmaStage.Session.Clarification;
using GemmaStage.Session.CognitiveCycle;
using GemmaStage.Session.DeepDive;
using GemmaStage.Session.GroundTruthSummarizer;
using GemmaStage.Session.IdeaReflector;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.MainIdeaComparator;
using GemmaStage.Session.Native;
using GemmaStage.Session.Perceptor;
using GemmaStage.Session.Resilience;
using GemmaStage.Session.Stores;
using GemmaStage.Session.TranscriptSummarizer;
using System;
using System.Linq;

namespace GemmaStage.Session;

public sealed class Session : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly AsrConversation _asr;
    private readonly I2tConversation _i2t;
    private readonly IdeaReflectorCoordinator _ideaReflector;
    private readonly InquirerConversation _inquirer;
    private readonly CognitiveCoordinator _orchestrator;
    private readonly Action<string>? _warn;
    private bool _ended;
    private bool _cancelled;
    private bool _disposed;
    private bool _failed;

    private Session(
        SessionConfig config,
        EngineHandle engine,
        AsrConversation asr,
        I2tConversation i2t,
        IdeaReflectorCoordinator ideaReflector,
        InquirerConversation inquirer,
        CycleScheduler scheduler,
        TranscriptStore transcripts,
        ImageStore images,
        MetricsStore metrics,
        RawContentBuffer rawContent,
        ConcernStore concerns,
        ConcernArchive concernArchive,
        InputQueue queue,
        RetellingsHistory retellingsHistory,
        QaRoundsHistory qaRoundsHistory,
        SessionEvents events,
        Action<string>? warn)
    {
        Config = config;
        _engine = engine;
        _asr = asr;
        _i2t = i2t;
        _ideaReflector = ideaReflector;
        _inquirer = inquirer;
        _warn = warn;
        TranscriptStore = transcripts;
        ImageStore = images;
        MetricsStore = metrics;
        RawContentBuffer = rawContent;
        ConcernStore = concerns;
        ConcernArchive = concernArchive;
        InputQueue = queue;
        RetellingsHistory = retellingsHistory;
        QaRoundsHistory = qaRoundsHistory;
        Scheduler = scheduler;
        Events = events;

        _orchestrator = new CognitiveCoordinator(
            asr,
            i2t,
            ideaReflector,
            inquirer,
            queue,
            transcripts,
            images,
            metrics,
            rawContent,
            concerns,
            concernArchive,
            retellingsHistory,
            qaRoundsHistory,
            scheduler,
            warn: warn,
            events: events);
    }

    public SessionConfig Config { get; }

    // Exposed so test/PoC harnesses can build extra conversations on top of the
    // engine (e.g. a SpeakerConversation that simulates Q&A answers). Game code
    // must not rely on this.
    public EngineHandle Engine => _engine;

    public SessionEvents Events { get; }

    public TranscriptStore TranscriptStore { get; }

    public ImageStore ImageStore { get; }

    public MetricsStore MetricsStore { get; }

    public RawContentBuffer RawContentBuffer { get; }

    public ConcernStore ConcernStore { get; }

    public ConcernArchive ConcernArchive { get; }

    public InputQueue InputQueue { get; }

    public RetellingsHistory RetellingsHistory { get; }

    public QaRoundsHistory QaRoundsHistory { get; }

    // Snapshot of the audience-side recall (topic + thesis + per-domain claims).
    // During the live phase the thesis is empty and DomainClaims hold joined
    // multi-claim text; after RunIdeaComprehension consolidates the live
    // accumulator, the thesis carries the inferred listener thesis and each
    // DomainClaim.Claim is the per-domain rephrased sentence.
    public IdeaReflector.AudienceSideRecall AudienceRecall => _ideaReflector.AudienceRecall;

    public CycleScheduler Scheduler { get; }

    public int CycleCount => _orchestrator.CycleCount;

    public bool IsPaused => _orchestrator.IsPaused;

    public bool IsOnLiveQAActive => _orchestrator.IsOnLiveQAActive;

    public bool IsInFinalQARound => _orchestrator.IsInFinalQARound;

    public bool IsEnded => _ended;

    public bool IsCancelled => _cancelled;

    public string BackendName => GemmaStageNative.EngineGetBackendName(_engine);

    public static Session Start(SessionConfig config, Action<string>? warn = null)
    {
        Guard.NotNull(config);
        Guard.NotNullOrWhiteSpace(config.ModelPath);

        GemmaStageNative.SetMinLogLevel(config.NativeLogLevel);

        using var settings = GemmaStageNative.EngineSettingsCreate(config.ModelPath, config.MmprojPath, config.Backend);
        GemmaStageNative.EngineSettingsSetContextSize(settings, config.ContextSize);
        GemmaStageNative.EngineSettingsSetGpuLayers(settings, config.GpuLayers);

        if (config.EnableBenchmark)
        {
            GemmaStageNative.EngineSettingsEnableBenchmark(settings);
        }

        EngineHandle? engine = null;
        AsrConversation? asr = null;
        I2tConversation? i2t = null;
        IdeaReflectorCoordinator? ideaReflector = null;
        InquirerConversation? inquirer = null;
        try
        {
            engine = GemmaStageNative.EngineCreate(settings);

            var transcripts = new TranscriptStore();
            var images = new ImageStore();
            var metrics = new MetricsStore();
            var rawContent = new RawContentBuffer();
            var concerns = new ConcernStore();
            var concernArchive = new ConcernArchive();
            var queue = new InputQueue();
            var retellingsHistory = new RetellingsHistory();
            var qaRoundsHistory = new QaRoundsHistory();
            var events = new SessionEvents();

            asr = new AsrConversation(engine, config.Language, warn);
            i2t = new I2tConversation(engine, warn);
            ideaReflector = new IdeaReflectorCoordinator(
                engine,
                config.InitialTopic,
                warn: warn);
            inquirer = new InquirerConversation(
                engine,
                openConcerns: null,
                concernCapacity: config.ConcernCapacity,
                warn: warn);

            var scheduler = new CycleScheduler(config.CycleMinElapsed);

            return new Session(
                config,
                engine,
                asr,
                i2t,
                ideaReflector,
                inquirer,
                scheduler,
                transcripts,
                images,
                metrics,
                rawContent,
                concerns,
                concernArchive,
                queue,
                retellingsHistory,
                qaRoundsHistory,
                events,
                warn);
        }
        catch
        {
            inquirer?.Dispose();
            ideaReflector?.Dispose();
            i2t?.Dispose();
            asr?.Dispose();
            engine?.Dispose();
            throw;
        }
    }

    public PerceptorTurnResult? PushAudio(byte[] wav, TimeSpan? inputDuration = null)
    {
        Guard.NotNull(wav);
        ThrowIfInputBlocked();
        return RunGuarded(() => _orchestrator.PushAudio(wav, inputDuration));
    }

    public PerceptorTurnResult? PushImage(byte[] png)
    {
        Guard.NotNull(png);
        ThrowIfInputBlocked();
        return RunGuarded(() => _orchestrator.PushImage(png));
    }

    public CognitiveCycleResult RunCycle()
    {
        ThrowIfUnavailable();
        return RunGuarded(() => _orchestrator.RunCycle());
    }

    // Open an On-Live Q&A round. The caller must push any in-flight audio
    // through PushAudio before invoking this; the audio chunker remains the
    // single upstream of Asr. The full InquirerConcern (not just the text)
    // is required so the round's originating concern ID is recorded for the
    // post-performance Q&A Handling evaluator.
    public Stores.RawContentSegment BeginOnLiveQARound(InquirerConcern concern)
    {
        ThrowIfUnavailable();
        return _orchestrator.BeginOnLiveQARound(concern);
    }

    // Close the active On-Live Q&A round and run one cycle over the buffered
    // question + answer + closing marker. Caller must push any trailing
    // in-flight answer audio through PushAudio first.
    public CognitiveCycleResult EndOnLiveQARound()
    {
        ThrowIfUnavailable();
        return RunGuarded(() => _orchestrator.EndOnLiveQARound());
    }

    public InquirerStateSnapshot SnapshotCognitiveState()
    {
        ThrowIfDisposed();
        return _inquirer.SnapshotState(_ideaReflector.Topic, _ideaReflector.MainIdeaUnderstanding);
    }

    // End the live phase. Runs one final cognitive cycle over whatever is
    // in the raw content buffer, archives any remaining open concerns, and
    // fires SessionEnded. After this the live cycle scheduler is finished;
    // post-performance roles (Clarification, TranscriptSummarizer, etc.) can
    // run, and Final Q&A rounds may push answer audio when EnableFinalQA is
    // on.
    //
    // The caller must deliver any in-flight live audio through PushAudio
    // before calling this - the audio chunker stays the single upstream of
    // Asr, and this method does not feed audio itself.
    public CognitiveCycleResult EndLivePhase()
    {
        ThrowIfDisposed();
        if (_ended)
        {
            throw new InvalidOperationException("The live phase has already ended.");
        }

        _ended = true;

        var finalCycle = RunGuarded(() => _orchestrator.RunFinalCycle());
        ArchiveRemainingOpenConcerns();
        Events.FireSessionEnded();
        return finalCycle;
    }

    // Open a Final Q&A round. Caller must have ended the live phase and run
    // Clarification first; the concern is built from one of
    // ClarificationResult.UnresolvedQuestions (or the full RevisedQuestions
    // list if the caller wants to ask resolved questions too). PushAudio
    // and PushImage are allowed for the duration of the round so the user's
    // answer accumulates into the same buffer as the question marker.
    public Stores.RawContentSegment BeginFinalQARound(InquirerConcern concern)
    {
        ThrowIfDisposed();
        if (!_ended)
        {
            throw new InvalidOperationException("Final Q&A rounds require the live phase to have ended (call EndLivePhase first).");
        }
        return _orchestrator.BeginFinalQARound(concern);
    }

    // Close the active Final Q&A round and run one restricted-Inquirer
    // cycle over the buffered question + answer + closing marker. Caller
    // must push any trailing in-flight answer audio through PushAudio first.
    public CognitiveCycleResult EndFinalQARound()
    {
        ThrowIfDisposed();
        return RunGuarded(() => _orchestrator.EndFinalQARound());
    }

    // Load Clarification's unresolved questions into the Inquirer so restricted
    // cycles during Final Q&A can track which concerns the user's answers resolve.
    // Call this after RunClarification() and before the first BeginFinalQARound().
    // No-op when Clarification did not run or produced no unresolved questions.
    public void LoadConcernsForFinalQA(ClarificationResult clarification)
    {
        ThrowIfDisposed();
        ThrowIfNotEnded();
        Guard.NotNull(clarification);

        if (!clarification.Ran || clarification.UnresolvedQuestions.Count == 0)
            return;

        var concerns = clarification.UnresolvedQuestions
            .Select(static q => new InquirerConcern(q.Id, q.Question, q.Type));
        _inquirer.LoadConcernsForFinalQA(concerns);
    }

    // Run the bundled post-performance pipeline: TranscriptSummarizer ->
    // GroundTruthSummarizer -> MainIdeaComparator -> DeepDive. Optional
    // clarification feeds the DeepDive prefill (so concerns_resolved counts
    // the Final Q&A walk). The live phase must already have ended; if Final
    // Q&A was enabled the caller is expected to have driven any rounds first.
    public PostPerformanceResult RunPostPerformancePipeline(ClarificationResult? clarification = null)
    {
        ThrowIfDisposed();
        ThrowIfNotEnded();

        return RunGuarded(() =>
        {
            var summarizer = RunTranscriptSummarizer();

            // IdeaComprehension consolidates the live multi-claim accumulator
            // into per-domain sentences + a thesis. Must run before GT so the
            // GT MAP audience seed sees consolidated names + sentences.
            var ideaComprehension = RunIdeaComprehension();

            var groundTruth = RunGroundTruthSummarizer();
            var comparator = RunMainIdeaComparator(summarizer.ReduceOutput, groundTruth);

            DeepDiveAggregateTurnResult? deepDive = null;
            if (summarizer.ReduceOutput is not null)
            {
                deepDive = RunDeepDive(summarizer, clarification, comparator: comparator);
            }

            return new PostPerformanceResult(summarizer, groundTruth, comparator, deepDive, ideaComprehension);
        });
    }

    // Convenience bundled call for the EnableFinalQA=false path: ends the
    // live phase and runs the post-performance pipeline in one shot.
    // Throws when EnableFinalQA=true - that path is interactive and the
    // caller must drive EndLivePhase + RunClarification + Begin/EndFinalQARound +
    // RunPostPerformancePipeline themselves.
    public FinalQAFinalizeResult GoFinalQAIfEnabledAndFinalize()
    {
        ThrowIfDisposed();
        if (_ended)
        {
            throw new InvalidOperationException("The session has already been finalized.");
        }
        if (Config.EnableFinalQA)
        {
            throw new InvalidOperationException(
                "GoFinalQAIfEnabledAndFinalize is for the EnableFinalQA=false path only. " +
                "When Final Q&A is enabled, the caller must drive: EndLivePhase, RunClarification, " +
                "Begin/EndFinalQARound per revised question, then RunPostPerformancePipeline.");
        }

        var finalCycle = EndLivePhase();
        var post = RunPostPerformancePipeline(clarification: null);
        return new FinalQAFinalizeResult(
            finalCycle,
            Clarification: null,
            post.TranscriptSummarizer,
            post.GroundTruthSummarizer,
            post.MainIdeaComparator,
            post.DeepDive,
            post.IdeaComprehension);
    }

    // Cancels any native conversation currently in flight and releases the
    // session resources. Unity uses this when the user dismisses the analyzing
    // screen and returns to the Lobby.
    public void Cancel()
    {
        if (_disposed)
        {
            return;
        }

        _cancelled = true;
        _engine.CancelActiveConversations();
        _engine.WaitForConversationSlotToDrain();
        Dispose();
    }

    public TranscriptSummarizerResult RunTranscriptSummarizer()
    {
        ThrowIfDisposed();
        ThrowIfNotEnded();

        return RunGuarded(() =>
        {
            // The transcript-side main idea is now driven by MAP-derived content
            // density (REDUCE prompt), not by talk duration. The deterministic
            // global_summary is built by TranscriptSummarizerRunner from MAP summaries so
            // delivery roughness is preserved instead of smoothed away.
            var runner = new TranscriptSummarizerRunner(_engine, Config.TranscriptSummarizerTokenBudget, _warn);
            var result = runner.Run(
                TranscriptStore,
                MetricsStore,
                Events.FireTranscriptSummarizerMapCompleted,
                Events.FireTranscriptSummarizerReduceCompleted,
                QaRoundsHistory);
            Events.FireTranscriptSummarizerCompleted(result);
            return result;
        });
    }

    // Returns GroundTruthSummarizerResult.Skipped when no ground-truth file is
    // attached, the file is missing, or its contents are empty. The comparator
    // (T8) treats that as "no ground-truth anchor available" and falls back to
    // the transcript-side main idea.
    public GroundTruthSummarizerResult RunGroundTruthSummarizer()
    {
        ThrowIfDisposed();
        ThrowIfNotEnded();

        return RunGuarded(() =>
        {
            var runner = new GroundTruthSummarizerRunner(_engine, Config.TranscriptSummarizerTokenBudget, _warn);
            var result = runner.Run(Config.GroundTruthInput);
            Events.FireGroundTruthSummarizerCompleted(result);
            return result;
        });
    }

    // Infers the listener's thesis from the cumulative main_idea_understanding
    // at end-of-live. Sets AudienceSideRecall.Thesis for the comparator.
    // Returns null only when the inference call itself failed.
    public IdeaReflector.IdeaComprehensionTurnResult? RunIdeaComprehension()
    {
        ThrowIfDisposed();
        ThrowIfNotEnded();

        return RunGuarded<IdeaReflector.IdeaComprehensionTurnResult?>(() =>
        {
            using var conv = new IdeaReflector.IdeaComprehensionConversation(_engine, _warn);
            var turn = conv.Infer(_ideaReflector.Topic, _ideaReflector.MainIdeaText);
            if (turn.Parse.Output is { } output)
            {
                _ideaReflector.ApplyThesis(output.Thesis);
            }
            else
            {
                _warn?.Invoke($"IdeaComprehension produced no thesis: {turn.Parse.Error ?? "(no error)"}");
            }
            return turn;
        });
    }

    public MainIdeaComparatorResult RunMainIdeaComparator(
        TranscriptSummarizerReduceOutput? transcriptSummary,
        GroundTruthSummarizerResult? groundTruth)
    {
        ThrowIfDisposed();
        ThrowIfNotEnded();
        _ = transcriptSummary; // kept for caller compat; comparator no longer uses it (GT-required).

        return RunGuarded(() =>
        {
            var runner = new MainIdeaComparatorRunner(_engine, _warn);
            var result = runner.Run(_ideaReflector.AudienceRecall, groundTruth?.Output);
            Events.FireMainIdeaComparatorCompleted(result);
            return result;
        });
    }

    // Caller owns the final-Q&A condition: invoke this only when
    // Config.EnableFinalQA (or equivalent Unity setup) allows Clarification.
    // Returns ClarificationResult.Skipped only when the concern archive is empty.
    public ClarificationResult RunClarification()
    {
        ThrowIfDisposed();
        ThrowIfNotEnded();

        return RunGuarded(() =>
        {
            var runner = new ClarificationRunner(_engine, Config.ClarificationTokenBudget, _warn);
            var result = runner.Run(ConcernArchive, TranscriptStore, MetricsStore);
            Events.FireClarificationCompleted(result);
            return result;
        });
    }

    public DeepDiveAggregateTurnResult RunDeepDive(
        TranscriptSummarizerResult transcriptSummary,
        ClarificationResult? clarification = null,
        string? finalMainIdeaOverride = null,
        MainIdeaComparatorResult? comparator = null)
    {
        Guard.NotNull(transcriptSummary);
        if (transcriptSummary.ReduceOutput is null)
        {
            throw new InvalidOperationException(
                "RunDeepDive requires a TranscriptSummarizerResult whose REDUCE stage produced an inferred main idea.");
        }
        ThrowIfDisposed();
        ThrowIfNotEnded();

        return RunGuarded(() => RunDeepDiveCore(transcriptSummary, clarification, finalMainIdeaOverride, comparator));
    }

    private DeepDiveAggregateTurnResult RunDeepDiveCore(
        TranscriptSummarizerResult transcriptSummary,
        ClarificationResult? clarification,
        string? finalMainIdeaOverride,
        MainIdeaComparatorResult? comparator)
    {
        var audienceRecallText = finalMainIdeaOverride
            ?? _ideaReflector.MainIdeaUnderstanding
            ?? "[no audience-side recall available]";

        var anchorOutput = comparator?.Output;

        var inputs = DeepDiveInputBuilder.Build(
            transcriptSummary.ReduceOutput!,
            audienceRecallText,
            transcriptSummary.MapOutputs,
            RetellingsHistory,
            MetricsStore,
            TranscriptStore,
            QaRoundsHistory,
            clarification,
            anchorOutput);

        var coordinator = new DeepDiveCoordinator(
            _engine,
            onSubRoleCompleted: Events.FireDeepDiveSubRoleCompleted,
            warn: _warn);

        var aggregate = coordinator.Run(inputs);
        Events.FireDeepDiveCompleted(aggregate.Result);
        return aggregate;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _inquirer.Dispose();
        _ideaReflector.Dispose();
        _i2t.Dispose();
        _asr.Dispose();
        _engine.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (_ended)
        {
            throw new InvalidOperationException("The session has already ended.");
        }
    }

    // Same as ThrowIfUnavailable, but allows audio / image input during a
    // Final Q&A round even though the live phase has ended (the user is
    // answering one of the revised questions).
    private void ThrowIfInputBlocked()
    {
        ThrowIfDisposed();
        if (_ended && !_orchestrator.IsInFinalQARound)
        {
            throw new InvalidOperationException(
                "The live phase has ended. PushAudio/PushImage are only allowed during a Final Q&A round.");
        }
    }

    private void ThrowIfNotEnded()
    {
        if (!_ended)
        {
            throw new InvalidOperationException("Finalize the module session before running post-performance pipeline steps.");
        }
    }

    private void ArchiveRemainingOpenConcerns()
    {
        var archivedAt = DateTimeOffset.UtcNow;
        var archivedConcerns = _inquirer.ArchiveOpenConcerns();

        ConcernStore.ReplaceOpen(Array.Empty<InquirerConcern>(), archivedAt);
        if (archivedConcerns.Count == 0)
        {
            return;
        }

        ConcernArchive.AppendSessionEnd(
            archivedConcerns.Select(concern =>
                new ArchivedConcernEntry(
                    concern.Id,
                    concern.Question,
                    concern.Type,
                    archivedAt,
                    ConcernArchive.SessionEndReason)));
    }

    private void ThrowIfDisposed()
    {
        Guard.NotDisposed(_disposed, this);
    }

    // Wrap any LLM-driven entry point so a ToolCallFailureException surfaces
    // as a SessionFailed event before propagating. The flag keeps the event
    // single-shot when nested entry points both bubble the same exception.
    private T RunGuarded<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ToolCallFailureException ex) when (!_failed)
        {
            _failed = true;
            Events.FireSessionFailed(new SessionFailure(ex.Role, ex.Message, ex));
            throw;
        }
    }
}

// Bundle returned by Session.GoFinalQAIfEnabledAndFinalize. FinalCycle carries
// the end-of-live-phase cycle that absorbs any in-flight audio plus the trailing
// raw content buffer. Clarification is null when EnableFinalQA was false;
// GroundTruthSummarizer.Ran is false when no GT file was attached (or it was
// missing/empty); DeepDive is null when TranscriptSummarizer's REDUCE stage
// failed and no transcript summary was produced.
public sealed record FinalQAFinalizeResult(
    CognitiveCycle.CognitiveCycleResult FinalCycle,
    Clarification.ClarificationResult? Clarification,
    TranscriptSummarizer.TranscriptSummarizerResult TranscriptSummarizer,
    GroundTruthSummarizer.GroundTruthSummarizerResult GroundTruthSummarizer,
    MainIdeaComparator.MainIdeaComparatorResult MainIdeaComparator,
    DeepDive.DeepDiveAggregateTurnResult? DeepDive,
    IdeaReflector.IdeaComprehensionTurnResult? IdeaComprehension);

// Bundle returned by Session.RunPostPerformancePipeline. GroundTruthSummarizer.Ran
// is false when no GT file was attached (or it was missing/empty); DeepDive is
// null when TranscriptSummarizer's REDUCE stage failed and no transcript
// summary was produced. IdeaComprehension is null only if the consolidation
// call failed.
public sealed record PostPerformanceResult(
    TranscriptSummarizer.TranscriptSummarizerResult TranscriptSummarizer,
    GroundTruthSummarizer.GroundTruthSummarizerResult GroundTruthSummarizer,
    MainIdeaComparator.MainIdeaComparatorResult MainIdeaComparator,
    DeepDive.DeepDiveAggregateTurnResult? DeepDive,
    IdeaReflector.IdeaComprehensionTurnResult? IdeaComprehension);
