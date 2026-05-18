using System;

namespace GemmaStage.Core
{
    /// <summary>
    /// Process-global pub/sub used by dev/test affordances to wake up systems
    /// without forcing a hard module dependency. Lives in Core because asmdefs
    /// at the edges (Audience, LiveQA, ...) all depend on Core but cannot
    /// reference each other directly.
    ///
    /// Strictly for dev tooling — not a substitute for the real session-event
    /// surface (<see cref="GemmaStage.Session.SessionLayer"/>).
    /// </summary>
    public static class DevSignals
    {
        // Raised by DevAudiencePanel's "Random Raise hand" button to drive a
        // manual smoke test of the Live Q&A popup without a real session.
        // LiveQaController subscribes and routes to its DevForceShow flow.
        public static event Action LiveQaPopupRequested;

        public static void RequestLiveQaPopup() => LiveQaPopupRequested?.Invoke();

        // Raised by DevAudiencePanel's "Start Final QA" button to drive a
        // manual smoke test of the Final Q&A spinner → questions flow without
        // a real session. FinalQaController subscribes and routes to its
        // dev path (shows spinner, then synthesises a ClarificationCompleted
        // event with sample questions after a short delay).
        public static event Action FinalQaPhaseRequested;

        public static void RequestFinalQaPhase() => FinalQaPhaseRequested?.Invoke();

        // Smoke-test hook for the End Session confirmation popup — lets dev
        // panels open it without a real B/Y press. EndSessionController subscribes.
        public static event Action EndSessionPopupRequested;

        public static void RequestEndSessionPopup() => EndSessionPopupRequested?.Invoke();

        // Triggers EvaluationProgressController.BeginEvaluation() and starts
        // a fake pipeline coroutine with scripted delays so the panel can be
        // smoke-tested without a real session.
        public static event Action BeginEvaluationRequested;

        public static void RequestBeginEvaluation() => BeginEvaluationRequested?.Invoke();
    }
}
