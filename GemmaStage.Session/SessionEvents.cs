using GemmaStage.Session.Clarification;
using GemmaStage.Session.CognitiveCycle;
using GemmaStage.Session.DeepDive;
using GemmaStage.Session.GroundTruthSummarizer;
using GemmaStage.Session.IdeaReflector;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.MainIdeaComparator;
using GemmaStage.Session.Perceptor;
using GemmaStage.Session.Resilience;
using GemmaStage.Session.TranscriptSummarizer;
using System;
using System.Collections.Generic;

namespace GemmaStage.Session;

// Event surface exposed by Session. Consumers (Unity, PoC, tests) subscribe
// to observe the live phase and post-performance pipeline without polling snapshots.
//
// Live-phase events fire from within Session.PushAudio / Session.PushImage
// (via the CognitiveCoordinator). Post-performance events fire from the pipeline
// methods on Session (RunTranscriptSummarizer / RunClarification / RunDeepDive).
//
// Handlers run synchronously on whatever thread drives Session. Keep them
// lightweight; long-running work belongs on a worker thread.
public sealed class SessionEvents
{
    // A Perceptor turn completed — audio, image, or drained queued input. Fires
    // for every turn, including those triggered inside a cycle drain.
    public event Action<PerceptorTurnResult>? PerceptorTurnCompleted;

    // A cognitive cycle is about to begin. Argument is the 1-based cycle index.
    // Useful for UI cues (audience "thinking" animation) before the two roles
    // run sequentially.
    public event Action<int>? CycleStarting;

    // A per-role turn within a cycle completed.
    public event Action<IdeaReflectorTurnResult>? IdeaReflectorTurnCompleted;
    public event Action<InquirerTurnResult>? InquirerTurnCompleted;

    // A cognitive cycle completed. Carries the full CognitiveCycleResult including
    // each role's turn, the state transition, and queue drain metrics.
    public event Action<CognitiveCycleResult>? CycleCompleted;

    // Fires after every cognitive cycle (including when the open set is empty
    // and including the End-Session final cycle). The game layer subscribes to
    // drive the On-Live Q&A panel: when EnableLiveQA is on and the open set
    // is non-empty, prompt the user; otherwise ignore the event.
    public event Action<OpenConcernsEvent>? OpenConcernsUpdated;

    // The module session has been finalized and remaining concerns have been archived.
    public event Action? SessionEnded;

    // Post-performance pipeline stage results.
    public event Action<TranscriptSummarizerMapStageResult>? TranscriptSummarizerMapCompleted;
    public event Action<TranscriptSummarizerReduceStageResult>? TranscriptSummarizerReduceCompleted;
    public event Action<TranscriptSummarizerResult>? TranscriptSummarizerCompleted;
    public event Action<GroundTruthSummarizerResult>? GroundTruthSummarizerCompleted;
    public event Action<MainIdeaComparatorResult>? MainIdeaComparatorCompleted;
    public event Action<ClarificationResult>? ClarificationCompleted;

    // Fires once per DeepDive sub-role (seven total). Carries the
    // criterion enum identifying which evaluation criterion was
    // produced and the parsed score+verdict. UI surfaces this to tick
    // an evaluation progress panel as each sub-role completes; the
    // terminal DeepDiveCompleted event still fires once with the
    // composed aggregate result.
    public event Action<DeepDiveSubRole, DeepDiveCriterion>? DeepDiveSubRoleCompleted;
    public event Action<DeepDiveResult>? DeepDiveCompleted;

    // The session aborted because a role's tool-call retries were exhausted.
    // Unity surfaces a "Something went wrong" popup that returns to the Lobby.
    public event Action<SessionFailure>? SessionFailed;

    internal void FirePerceptorTurnCompleted(PerceptorTurnResult turn)
    {
        PerceptorTurnCompleted?.Invoke(turn);
    }

    internal void FireCycleStarting(int index)
    {
        CycleStarting?.Invoke(index);
    }

    internal void FireIdeaReflectorTurnCompleted(IdeaReflectorTurnResult turn)
    {
        IdeaReflectorTurnCompleted?.Invoke(turn);
    }

    internal void FireInquirerTurnCompleted(InquirerTurnResult turn)
    {
        InquirerTurnCompleted?.Invoke(turn);
    }

    internal void FireCycleCompleted(CognitiveCycleResult result)
    {
        CycleCompleted?.Invoke(result);
    }

    internal void FireOpenConcernsUpdated(OpenConcernsEvent payload)
    {
        OpenConcernsUpdated?.Invoke(payload);
    }

    internal void FireSessionEnded()
    {
        SessionEnded?.Invoke();
    }

    internal void FireTranscriptSummarizerMapCompleted(TranscriptSummarizerMapStageResult result)
    {
        TranscriptSummarizerMapCompleted?.Invoke(result);
    }

    internal void FireTranscriptSummarizerReduceCompleted(TranscriptSummarizerReduceStageResult result)
    {
        TranscriptSummarizerReduceCompleted?.Invoke(result);
    }

    internal void FireTranscriptSummarizerCompleted(TranscriptSummarizerResult result)
    {
        TranscriptSummarizerCompleted?.Invoke(result);
    }

    internal void FireGroundTruthSummarizerCompleted(GroundTruthSummarizerResult result)
    {
        GroundTruthSummarizerCompleted?.Invoke(result);
    }

    internal void FireMainIdeaComparatorCompleted(MainIdeaComparatorResult result)
    {
        MainIdeaComparatorCompleted?.Invoke(result);
    }

    internal void FireClarificationCompleted(ClarificationResult result)
    {
        ClarificationCompleted?.Invoke(result);
    }

    internal void FireDeepDiveSubRoleCompleted(DeepDiveSubRole role, DeepDiveCriterion criterion)
    {
        DeepDiveSubRoleCompleted?.Invoke(role, criterion);
    }

    internal void FireDeepDiveCompleted(DeepDiveResult result)
    {
        DeepDiveCompleted?.Invoke(result);
    }

    internal void FireSessionFailed(SessionFailure failure)
    {
        SessionFailed?.Invoke(failure);
    }
}

public sealed record OpenConcernsEvent(
    int CycleIndex,
    IReadOnlyList<InquirerConcern> OpenConcerns);

public sealed record SessionFailure(string Role, string Message, Exception Exception);
