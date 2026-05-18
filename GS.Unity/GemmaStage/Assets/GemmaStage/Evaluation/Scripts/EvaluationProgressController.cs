using System.Collections;
using GemmaStage.Core;
using GemmaStage.Results;
using GemmaStage.Session;
using UnityEngine;

namespace GemmaStage.Evaluation
{
    /// <summary>
    /// Drives the Evaluation progress panel after the live phase and any
    /// Final Q&A phase end. Subscribes to the five SessionLayer evaluation
    /// events (TranscriptSummarizerMap → Reduce → GroundTruthSummarizer →
    /// MainIdeaComparator → DeepDive) and advances a progress bar as each
    /// stage completes. Provides the Cancel affordance per GAME_DESIGN §8.1.
    ///
    /// Far-panel rule: pushes a far-panel hold on RayController while the
    /// panel is visible so the active-hand ray is on for the Cancel button.
    /// See PHASE_5_TASKS.md §5.11 and GAME_DESIGN.md §8.1.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EvaluationProgressController : MonoBehaviour
    {
        // Progress bar target values per pipeline stage (0–1).
        const float ProgressSummarizerMap    = 0.30f;
        const float ProgressSummarizerReduce = 0.50f;
        const float ProgressGroundTruth      = 0.60f;
        const float ProgressComparator       = 0.70f;
        const float ProgressDeepDive         = 1.00f;

        // Far panel at speaking distance, eye-level — the player uses the ray
        // to press Cancel (not a poke panel).
        const float PanelDistanceMeters = 1.5f;

        // Brief hold at 100 % so the filled bar is visible before hiding.
        const float DeepDiveHoldSeconds = 0.5f;

        // Delay between dev-fake pipeline stages (seconds, unscaled).
        const float DevStageDelayShort = 0.5f;
        const float DevStageDelayLong  = 1.0f;

        [SerializeField] SessionLayer          sessionLayer;
        [SerializeField] RayController         rayController;
        [SerializeField] SessionManager        sessionManager;
        [Tooltip("World-space EvaluationPanel prefab. Lazy-instantiated on first show, then reused.")]
        [SerializeField] EvaluationPanelView   panelPrefab;
        [Tooltip("Results controller shown after the evaluation panel finishes. Assign the ResultsController from Shared.unity.")]
        [SerializeField] ResultsController     resultsController;

        bool              _active;
        bool              _farHoldActive;
        EvaluationPanelView _panel;
        Coroutine         _devRoutine;
        Coroutine         _hideDelayRoutine;

        // ── Unity lifecycle ────────────────────────────────────────────────────

        void Awake()
        {
            if (sessionLayer  == null) sessionLayer  = FindAnyObjectByType<SessionLayer>();
            if (rayController == null) rayController = FindAnyObjectByType<RayController>();
            if (sessionManager == null) sessionManager = FindAnyObjectByType<SessionManager>();

            if (sessionLayer  == null) Debug.LogError("[EvaluationProgressController] SessionLayer missing.");
            if (rayController == null) Debug.LogError("[EvaluationProgressController] RayController missing.");
            if (sessionManager == null) Debug.LogError("[EvaluationProgressController] SessionManager missing.");
            if (panelPrefab   == null) Debug.LogError("[EvaluationProgressController] EvaluationPanel prefab not assigned.");
        }

        void OnEnable()
        {
            if (sessionLayer != null)
            {
                sessionLayer.TranscriptSummarizerMapCompleted    += HandleSummarizerMapCompleted;
                sessionLayer.TranscriptSummarizerReduceCompleted += HandleSummarizerReduceCompleted;
                sessionLayer.GroundTruthSummarizerCompleted      += HandleGroundTruthCompleted;
                sessionLayer.MainIdeaComparatorCompleted         += HandleComparatorCompleted;
                sessionLayer.DeepDiveCompleted                   += HandleDeepDiveCompleted;
                sessionLayer.SessionResultsAvailable             += HandleSessionResultsAvailable;
            }
            DevSignals.BeginEvaluationRequested += HandleDevBeginEvaluationRequested;
        }

        void OnDisable()
        {
            if (sessionLayer != null)
            {
                sessionLayer.TranscriptSummarizerMapCompleted    -= HandleSummarizerMapCompleted;
                sessionLayer.TranscriptSummarizerReduceCompleted -= HandleSummarizerReduceCompleted;
                sessionLayer.GroundTruthSummarizerCompleted      -= HandleGroundTruthCompleted;
                sessionLayer.MainIdeaComparatorCompleted         -= HandleComparatorCompleted;
                sessionLayer.DeepDiveCompleted                   -= HandleDeepDiveCompleted;
                sessionLayer.SessionResultsAvailable             -= HandleSessionResultsAvailable;
            }
            DevSignals.BeginEvaluationRequested -= HandleDevBeginEvaluationRequested;
            TeardownPanel();
        }

        // ── Public entry point ─────────────────────────────────────────────────

        /// <summary>
        /// Entry point called by SessionManager.BeginEvaluation(). Shows the
        /// panel at 0 % and begins listening for pipeline stage events.
        /// </summary>
        public void BeginEvaluation()
        {
            if (_active) return;
            _active = true;

            EnsurePanel();
            PositionPanelInFrontOfPlayer();

            rayController?.PushFarPanelHold();
            _farHoldActive = true;

            _panel.SetProgress(0f);
            _panel.Show();
        }

        // ── Stage event handlers ───────────────────────────────────────────────

        void HandleSummarizerMapCompleted()
        {
            if (!_active) return;
            _panel?.SetProgress(ProgressSummarizerMap);
        }

        void HandleSummarizerReduceCompleted()
        {
            if (!_active) return;
            _panel?.SetProgress(ProgressSummarizerReduce);
        }

        void HandleGroundTruthCompleted(GroundTruthSummarizerResult result)
        {
            // Jump to 60 % regardless of result.Ran — when GT was skipped the
            // progress controller still advances past the 50→60 gap
            // (SESSION_ARCHITECTURE §12.2 / PHASE_5_TASKS §5.11.2).
            if (!_active) return;
            _panel?.SetProgress(ProgressGroundTruth);
        }

        void HandleComparatorCompleted()
        {
            if (!_active) return;
            _panel?.SetProgress(ProgressComparator);
        }

        void HandleDeepDiveCompleted(DeepDiveResult result)
        {
            if (!_active) return;
            // Fill the bar to 100 %. The handoff to the Results panel happens
            // in HandleSessionResultsAvailable (fired right after the
            // post-performance pipeline returns) so the Results panel can
            // include the auxiliary data (transcript, audience thesis, …).
            _panel?.SetProgress(ProgressDeepDive);
        }

        void HandleSessionResultsAvailable(SessionResultsPayload payload)
        {
            if (!_active) return;
            // Brief hold at 100 % so the filled bar is visible, then transition
            // to the Results panel (Phase 7.1).
            if (_hideDelayRoutine != null) StopCoroutine(_hideDelayRoutine);
            _hideDelayRoutine = StartCoroutine(HidePanelAndShowResultsAfterDelay(DeepDiveHoldSeconds, payload));
        }

        // ── Cancel handler ─────────────────────────────────────────────────────

        void HandleCancelPressed()
        {
            // Tear down immediately (don't wait for hide animation).
            TeardownPanel();
            sessionManager?.CancelEvaluation();
        }

        // ── Internal helpers ───────────────────────────────────────────────────

        IEnumerator HidePanelAndShowResultsAfterDelay(float seconds, SessionResultsPayload payload)
        {
            yield return new WaitForSecondsRealtime(seconds);
            _hideDelayRoutine = null;
            TeardownPanel();
            resultsController?.ShowResults(payload);
        }

        void TeardownPanel()
        {
            CancelDevRoutine();

            if (_hideDelayRoutine != null)
            {
                StopCoroutine(_hideDelayRoutine);
                _hideDelayRoutine = null;
            }

            _active = false;
            ReleaseFarHold();
            if (_panel != null) _panel.Hide();
        }

        void ReleaseFarHold()
        {
            if (!_farHoldActive) return;
            _farHoldActive = false;
            rayController?.PopFarPanelHold();
        }

        void EnsurePanel()
        {
            if (_panel != null) return;
            if (panelPrefab == null) return;

            _panel = Instantiate(panelPrefab);
            _panel.gameObject.SetActive(false);
            _panel.OnCancelPressed += HandleCancelPressed;
        }

        void PositionPanelInFrontOfPlayer()
        {
            if (_panel == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            var camPos  = cam.transform.position;
            var forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            // Eye-level far panel — the player reads and clicks Cancel via ray.
            var pos = camPos + forward * PanelDistanceMeters;
            pos.y   = camPos.y;
            _panel.transform.position = pos;
        }

        // ── Dev smoke test ─────────────────────────────────────────────────────

        void HandleDevBeginEvaluationRequested()
        {
            if (_active) return;
            BeginEvaluation();
            CancelDevRoutine();
            _devRoutine = StartCoroutine(DevFakeEvaluation());
        }

        IEnumerator DevFakeEvaluation()
        {
            yield return new WaitForSecondsRealtime(DevStageDelayLong);
            sessionLayer?.RaiseTranscriptSummarizerMapCompleted();

            yield return new WaitForSecondsRealtime(DevStageDelayLong);
            sessionLayer?.RaiseTranscriptSummarizerReduceCompleted();

            yield return new WaitForSecondsRealtime(DevStageDelayShort);
            sessionLayer?.RaiseGroundTruthSummarizerCompleted(new GroundTruthSummarizerResult(ran: true));

            yield return new WaitForSecondsRealtime(DevStageDelayShort);
            sessionLayer?.RaiseMainIdeaComparatorCompleted();

            yield return new WaitForSecondsRealtime(DevStageDelayLong);
            // ForDev(withComparator: true) to also smoke-test the GT comparator section.
            var devDeep = DeepDiveResult.ForDev(withComparator: true);
            sessionLayer?.RaiseDeepDiveCompleted(devDeep);
            sessionLayer?.RaiseSessionResultsAvailable(SessionResultsPayload.ForDev(devDeep));

            _devRoutine = null;
        }

        void CancelDevRoutine()
        {
            if (_devRoutine == null) return;
            StopCoroutine(_devRoutine);
            _devRoutine = null;
        }
    }
}
