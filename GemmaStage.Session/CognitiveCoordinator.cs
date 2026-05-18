using System.Diagnostics;
using System.Linq;
using GemmaStage.Session.CognitiveCycle;
using GemmaStage.Session.IdeaReflector;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.LiveQA;
using GemmaStage.Session.Perceptor;
using GemmaStage.Session.Stores;

namespace GemmaStage.Session;

// Drives IdeaReflector -> Inquirer cycles. A single driver thread calls
// PushAudio / PushImage / RunCycle sequentially; inputs that arrive while a
// cycle runs are queued and replayed afterwards. Cycles are audio-only -
// images only append to the raw content buffer.
public sealed class CognitiveCoordinator
{
    private readonly AsrConversation _asr;
    private readonly I2tConversation _i2t;
    private readonly IdeaReflectorCoordinator _ideaReflector;
    private readonly InquirerConversation _inquirer;
    private readonly InputQueue _queue;
    private readonly TranscriptStore _transcripts;
    private readonly ImageStore _images;
    private readonly MetricsStore _metrics;
    private readonly RawContentBuffer _rawContent;
    private readonly ConcernStore _concerns;
    private readonly ConcernArchive _concernArchive;
    private readonly RetellingsHistory _history;
    private readonly QaRoundsHistory _qaRounds;
    private readonly CycleScheduler _scheduler;
    private readonly Action<string>? _warn;
    private readonly TimeProvider _time;
    private readonly SessionEvents? _events;

    private int _cycleCount;
    private bool _paused;
    private bool _cycleInFlight;
    private bool _onLiveQAActive;
    private bool _inFinalQARound;
    private QaRoundHandle? _openLiveRound;
    private QaRoundHandle? _openFinalRound;

    public CognitiveCoordinator(
        AsrConversation asr,
        I2tConversation i2t,
        IdeaReflectorCoordinator ideaReflector,
        InquirerConversation inquirer,
        InputQueue queue,
        TranscriptStore transcripts,
        ImageStore images,
        MetricsStore metrics,
        RawContentBuffer rawContent,
        ConcernStore concerns,
        ConcernArchive concernArchive,
        RetellingsHistory history,
        QaRoundsHistory qaRounds,
        CycleScheduler scheduler,
        Action<string>? warn = null,
        TimeProvider? time = null,
        SessionEvents? events = null)
    {
        _asr = asr ?? throw new ArgumentNullException(nameof(asr));
        _i2t = i2t ?? throw new ArgumentNullException(nameof(i2t));
        _ideaReflector = ideaReflector ?? throw new ArgumentNullException(nameof(ideaReflector));
        _inquirer = inquirer ?? throw new ArgumentNullException(nameof(inquirer));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _rawContent = rawContent ?? throw new ArgumentNullException(nameof(rawContent));
        _concerns = concerns ?? throw new ArgumentNullException(nameof(concerns));
        _concernArchive = concernArchive ?? throw new ArgumentNullException(nameof(concernArchive));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _qaRounds = qaRounds ?? throw new ArgumentNullException(nameof(qaRounds));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _warn = warn;
        _time = time ?? TimeProvider.System;
        _events = events;
    }

    public int CycleCount => _cycleCount;

    public bool IsPaused => _paused;

    public bool IsOnLiveQAActive => _onLiveQAActive;

    public bool IsInFinalQARound => _inFinalQARound;

    public PerceptorTurnResult? PushAudio(byte[] wav, TimeSpan? inputDuration = null)
    {
        Guard.NotNull(wav);
        if (wav.Length == 0)
        {
            throw new ArgumentException("Audio payload cannot be empty.", nameof(wav));
        }
        if (inputDuration is { } duration && duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputDuration),
                inputDuration,
                "Input duration cannot be negative.");
        }

        if (_paused)
        {
            _queue.EnqueueAudio(wav, inputDuration);
            return null;
        }

        var turn = SendAudioAndPersist(wav, inputDuration);
        _events?.FirePerceptorTurnCompleted(turn);
        TriggerCycleIfArmed(turn);
        return turn;
    }

    // Image arrivals append an examination to the raw content buffer; the
    // next audio chunk_completed past the elapsed-time gate fires the cycle.
    public PerceptorTurnResult? PushImage(byte[] png)
    {
        Guard.NotNull(png);
        if (png.Length == 0)
        {
            throw new ArgumentException("Image payload cannot be empty.", nameof(png));
        }

        if (_paused)
        {
            _queue.EnqueueImage(png);
            return null;
        }

        var turn = SendImageAndPersist(png);
        _events?.FirePerceptorTurnCompleted(turn);
        return turn;
    }

    // End-Session entry point. Runs one final cognitive cycle over whatever has
    // accumulated in the raw content buffer. The caller is responsible for
    // delivering any in-flight audio through PushAudio before invoking this so
    // Asr remains the single audio sink; this method never feeds audio itself.
    public CognitiveCycleResult RunFinalCycle()
    {
        if (_cycleInFlight)
        {
            throw new InvalidOperationException("Cognitive cycle already in flight.");
        }

        return RunCycle();
    }

    public CognitiveCycleResult RunCycle()
    {
        return RunCycle(restrictedInquirer: false);
    }

    private CognitiveCycleResult RunCycle(bool restrictedInquirer)
    {
        if (_cycleInFlight)
        {
            throw new InvalidOperationException("Cognitive cycle already in flight.");
        }

        // _paused diverts new Push* calls into InputQueue instead of the conversations,
        // so the cycle's role prompts see a stable snapshot; the queued inputs are
        // replayed in DrainQueue after the cycle finishes.
        _cycleInFlight = true;
        _paused = true;
        try
        {
            var startedAt = _time.GetUtcNow();
            var topicBefore = _ideaReflector.Topic;
            var mainIdeaBefore = _ideaReflector.MainIdeaUnderstanding;
            var stateBefore = _inquirer.SnapshotState(topicBefore, mainIdeaBefore);
            var index = ++_cycleCount;
            _events?.FireCycleStarting(index);

            var cycleRawContent = _rawContent.Drain();

            string? error = null;

            // 1. IdeaReflector - System1Reactor produces a retelling from raw
            //    content + recent retellings + current main idea, then
            //    System2Reflector appends only the new claims. The combined
            //    turn carries both sub-turns plus the per-cycle reflection.
            var previousRetellings = _history.Snapshot()
                .Select(static e => e.Retelling)
                .ToList();
            var ideaTurn = _ideaReflector.Reflect(cycleRawContent, previousRetellings);
            _events?.FireIdeaReflectorTurnCompleted(ideaTurn);

            string? retellingText = null;
            if (ideaTurn.Parse.IsFailure || ideaTurn.Parse.Reflection is null)
            {
                error = ideaTurn.Parse.Error ?? "IdeaReflector produced no reflection.";
                Warn($"Cycle #{index}: {error}");
            }
            else
            {
                retellingText = ideaTurn.Parse.Reflection.Retelling;
            }

            var topicAfterIdea = ideaTurn.TopicAfter;
            var mainIdeaAfterIdea = ideaTurn.MainIdeaUnderstandingAfter;

            // 2. Inquirer - concerns-only. Reads ALL retellings produced this
            //    session (including the one just emitted) plus the just-updated
            //    main idea and current open concerns.
            InquirerTurnResult? inquirerTurn = null;
            if (error is null && retellingText is not null)
            {
                var allRetellings = new List<string>(previousRetellings.Count + 1);
                allRetellings.AddRange(previousRetellings);
                allRetellings.Add(retellingText);

                inquirerTurn = _inquirer.Reflect(
                    topicAfterIdea,
                    mainIdeaAfterIdea,
                    allRetellings,
                    restricted: restrictedInquirer);
                _events?.FireInquirerTurnCompleted(inquirerTurn);

                if (inquirerTurn.Parse.IsFailure || inquirerTurn.Parse.Reflection is null)
                {
                    error = inquirerTurn.Parse.Error ?? "Inquirer reflection failed.";
                    Warn($"Cycle #{index}: {error}");
                }
                else
                {
                    PersistCycle(
                        index,
                        inquirerTurn.Timestamp,
                        retellingText,
                        cycleRawContent,
                        ideaTurn.Parse.Reflection,
                        inquirerTurn.Parse.Reflection,
                        inquirerTurn.State,
                        inquirerTurn.ArchivedConcerns);
                }
            }

            var queueMetrics = DrainQueue();
            _scheduler.RecordCycle(_time.GetUtcNow());
            var stateAfter = _inquirer.SnapshotState(_ideaReflector.Topic, _ideaReflector.MainIdeaUnderstanding);

            var result = new CognitiveCycleResult(
                Index: index,
                StartedAt: startedAt,
                CompletedAt: _time.GetUtcNow(),
                RawContent: cycleRawContent,
                IdeaReflectorTurn: ideaTurn,
                InquirerTurn: inquirerTurn,
                StateBefore: stateBefore,
                StateAfter: stateAfter,
                Queue: queueMetrics,
                Error: error);

            _events?.FireCycleCompleted(result);
            _events?.FireOpenConcernsUpdated(new OpenConcernsEvent(index, stateAfter.Concerns));
            return result;
        }
        finally
        {
            _paused = false;
            _cycleInFlight = false;
        }
    }

    // Open an On-Live Q&A round. The caller is responsible for delivering any
    // in-flight audio through PushAudio first - the audio chunker is the
    // single upstream of Asr and this method never feeds audio. The chosen
    // concern's question text is appended as a marker, cycle scheduling is
    // suspended so the user's answer accumulates into the same buffer, and
    // the round is registered in QaRoundsHistory so post-performance roles
    // can later decide whether the answer resolved the concern.
    public RawContentSegment BeginOnLiveQARound(InquirerConcern concern)
    {
        Guard.NotNull(concern);
        if (_cycleInFlight)
        {
            throw new InvalidOperationException("Cannot begin an On-Live Q&A round while a cognitive cycle is in flight.");
        }
        if (_onLiveQAActive)
        {
            throw new InvalidOperationException("An On-Live Q&A round is already active.");
        }

        var openedAt = _time.GetUtcNow();
        _openLiveRound = _qaRounds.BeginRound(QaPhase.Live, concern, openedAt);
        _onLiveQAActive = true;
        return _rawContent.AppendQuestionMarker(concern.Question.Trim(), openedAt);
    }

    // Close the On-Live Q&A round. Caller must have delivered the user's
    // trailing in-flight audio through PushAudio before invoking this.
    // Appends the closing marker, exits Q&A mode, and runs one cycle
    // immediately so IdeaReflector consumes the question + answer + closing
    // marker as one buffer.
    public CognitiveCycleResult EndOnLiveQARound()
    {
        if (_cycleInFlight)
        {
            throw new InvalidOperationException("Cannot end an On-Live Q&A round while a cognitive cycle is in flight.");
        }
        if (!_onLiveQAActive)
        {
            throw new InvalidOperationException("No On-Live Q&A round is active.");
        }

        var closedAt = _time.GetUtcNow();
        _rawContent.AppendQaClosedMarker(LiveQARound.ClosingMarkerText, closedAt);
        if (_openLiveRound is not null)
        {
            _qaRounds.CloseRound(_openLiveRound, closedAt);
            _openLiveRound = null;
        }
        _onLiveQAActive = false;
        return RunCycle();
    }

    // Open a Final Q&A round. The caller (Session) is responsible for having
    // already ended the live phase and run Clarification; the concern here
    // is built from one of Clarification's revised questions. The marker
    // plus the user's answer audio flow into the same buffer; EndFinalQARound
    // runs a single restricted-Inquirer cycle over them.
    public RawContentSegment BeginFinalQARound(InquirerConcern concern)
    {
        Guard.NotNull(concern);
        if (_cycleInFlight)
        {
            throw new InvalidOperationException("Cannot begin a Final Q&A round while a cognitive cycle is in flight.");
        }
        if (_onLiveQAActive)
        {
            throw new InvalidOperationException("Cannot begin a Final Q&A round while an On-Live Q&A round is active.");
        }
        if (_inFinalQARound)
        {
            throw new InvalidOperationException("A Final Q&A round is already active.");
        }

        var openedAt = _time.GetUtcNow();
        _openFinalRound = _qaRounds.BeginRound(QaPhase.Final, concern, openedAt);
        _inFinalQARound = true;
        return _rawContent.AppendQuestionMarker(concern.Question.Trim(), openedAt);
    }

    // Close the Final Q&A round. Caller must have delivered the user's
    // trailing in-flight answer audio through PushAudio before invoking this.
    // Appends the closing marker, exits the round, and runs one cycle with
    // the Inquirer in restricted mode (new_concerns must be empty; only
    // resolved/irrelevant removals are allowed).
    public CognitiveCycleResult EndFinalQARound()
    {
        if (_cycleInFlight)
        {
            throw new InvalidOperationException("Cannot end a Final Q&A round while a cognitive cycle is in flight.");
        }
        if (!_inFinalQARound)
        {
            throw new InvalidOperationException("No Final Q&A round is active.");
        }

        var closedAt = _time.GetUtcNow();
        _rawContent.AppendQaClosedMarker(LiveQARound.ClosingMarkerText, closedAt);
        if (_openFinalRound is not null)
        {
            _qaRounds.CloseRound(_openFinalRound, closedAt);
            _openFinalRound = null;
        }
        _inFinalQARound = false;
        return RunCycle(restrictedInquirer: true);
    }

    private PerceptorTurnResult SendAudioAndPersist(byte[] wav, TimeSpan? inputDuration)
    {
        var turn = _asr.SendAudio(wav, inputDuration);
        if (!turn.Parse.IsFailure && turn.Parse.Audio is { } audio)
        {
            _transcripts.Append(new TranscriptEntry(turn.TurnSequence, turn.Timestamp, audio.Transcript));
            _metrics.Append(new MetricEntry(
                turn.TurnSequence,
                turn.Timestamp,
                audio.Clarity.ToWireString(),
                audio.Emotion?.ToWireString(),
                audio.Grammar?.ToWireString(),
                audio.ChunkCompleted,
                turn.Benchmark.TimeToFirstTokenSeconds,
                turn.Benchmark.DecodeTokensPerSecond,
                turn.Benchmark.DecodeTokenCount));

            if (!string.IsNullOrWhiteSpace(audio.Transcript))
            {
                _rawContent.AppendAudio(audio.Transcript, turn.Timestamp);
            }
        }

        return turn;
    }

    private PerceptorTurnResult SendImageAndPersist(byte[] png)
    {
        var turn = _i2t.SendImage(png, _ideaReflector.MainIdeaUnderstanding);
        if (!turn.Parse.IsFailure && turn.Parse.Image is { } image)
        {
            _images.Append(new ImageEntry(turn.TurnSequence, turn.Timestamp, image.Examination));

            if (!string.IsNullOrWhiteSpace(image.Examination))
            {
                _rawContent.AppendImage(image.Examination, turn.Timestamp);
            }
        }

        return turn;
    }

    private void TriggerCycleIfArmed(PerceptorTurnResult turn)
    {
        if (turn.InputDuration is { } inputDuration)
        {
            _scheduler.AdvanceAudio(inputDuration);
        }

        if (turn.Parse.IsFailure)
        {
            return;
        }

        var audio = turn.Parse.Audio;
        if (audio is null)
        {
            return;
        }

        // While the user is answering a concern (live Q&A or final Q&A),
        // audio still flows through Asr and into the buffer, but no cycle
        // fires until the matching End*Round closes the round and runs one
        // explicitly.
        if (_onLiveQAActive || _inFinalQARound)
        {
            return;
        }

        if (_scheduler.ShouldTriggerAfterAudio(audio.ChunkCompleted))
        {
            RunCycle();
        }
    }

    private CycleQueueMetrics DrainQueue()
    {
        var depthAtPause = _queue.Count;
        if (depthAtPause == 0)
        {
            return new CycleQueueMetrics(0, 0, TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();
        var drained = 0;
        while (_queue.TryDequeue(out var entry) && entry is not null)
        {
            try
            {
                var payload = entry.Payload.ToArray();
                PerceptorTurnResult? drainedTurn = null;
                switch (entry.Kind)
                {
                    case InputKind.Audio:
                        drainedTurn = SendAudioAndPersist(payload, entry.InputDuration);
                        break;
                    case InputKind.Image:
                        drainedTurn = SendImageAndPersist(payload);
                        break;
                    default:
                        Warn($"Cycle queue drain: unknown input kind {entry.Kind} (seq={entry.Sequence}).");
                        break;
                }

                if (drainedTurn is not null)
                {
                    _events?.FirePerceptorTurnCompleted(drainedTurn);
                }

                drained++;
            }
            catch (Exception ex)
            {
                Warn($"Cycle queue drain: failed to replay {entry.Kind} seq={entry.Sequence}: {ex.Message}");
            }
        }

        stopwatch.Stop();
        return new CycleQueueMetrics(depthAtPause, drained, stopwatch.Elapsed);
    }

    private void PersistCycle(
        int index,
        DateTimeOffset timestamp,
        string retelling,
        IReadOnlyList<RawContentSegment> rawContent,
        IdeaReflectorReflection? ideaReflectorReflection,
        InquirerReflection inquirerReflection,
        InquirerStateSnapshot stateAfter,
        IReadOnlyList<ArchivedConcern> archivedConcerns)
    {
        _history.Append(new RetellingHistoryEntry(
            index,
            timestamp,
            retelling,
            rawContent,
            IdeaReflectorOutput: ideaReflectorReflection,
            Reflection: inquirerReflection,
            stateAfter));

        _concerns.ReplaceOpen(stateAfter.Concerns, timestamp);

        if (archivedConcerns.Count > 0)
        {
            _concernArchive.AppendOverflow(
                archivedConcerns.Select(concern =>
                    new ArchivedConcernEntry(
                        concern.Id,
                        concern.Question,
                        concern.Type,
                        timestamp,
                        ConcernArchive.OverflowReason)));
        }
    }

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }
}
