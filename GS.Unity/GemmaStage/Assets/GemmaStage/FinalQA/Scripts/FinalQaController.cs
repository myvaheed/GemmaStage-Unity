using System.Collections;
using System.Collections.Generic;
using GemmaStage.Core;
using GemmaStage.Session;
using UnityEngine;

namespace GemmaStage.FinalQA
{
    /// <summary>
    /// Drives the Final Q&A floating popup between End Session and the
    /// Evaluation phase. Shows the "Preparing questions…" spinner while
    /// Clarification runs, then walks the player through the unresolved
    /// revised questions one at a time with Next / Finish controls.
    /// See docs/SESSION_ARCHITECTURE.md §11 and docs/GAME_DESIGN.md §7.2.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FinalQaController : MonoBehaviour
    {
        const float PanelDistanceMeters = 0.4f;
        const float ChestDropFromEyeMeters = 0.2f;

        // Delay before the dev hook synthesises a ClarificationCompleted event
        // — long enough for the spinner to be visible.
        const float DevClarificationDelaySeconds = 1.5f;

        [SerializeField] SessionLayer sessionLayer;
        [Tooltip("World-space FinalQaPanel prefab (Assets/GemmaStage/FinalQA/UI/Prefabs/FinalQaPanel.prefab). Lazy-instantiated on first show, then reused.")]
        [SerializeField] FinalQaPanelView panelPrefab;

        bool _finalQaEnabled;

        bool _phaseActive;            // BeginFinalQAPhase → BeginEvaluation window
        IReadOnlyList<FinalQaQuestion> _questions;
        int _currentIndex;
        bool _roundOpen;              // true between BeginFinalQARound and EndFinalQARound

        FinalQaPanelView _panel;
        Coroutine _devClarificationRoutine;

        void Awake()
        {
            if (sessionLayer == null) sessionLayer = FindAnyObjectByType<SessionLayer>();

            if (sessionLayer == null) Debug.LogError("[FinalQaController] SessionLayer missing.");
            if (panelPrefab == null) Debug.LogError("[FinalQaController] FinalQaPanel prefab not assigned.");
        }

        void OnEnable()
        {
            if (sessionLayer != null)
                sessionLayer.ClarificationCompleted += HandleClarificationCompleted;
            DevSignals.FinalQaPhaseRequested += HandleDevPhaseRequested;
        }

        void OnDisable()
        {
            if (sessionLayer != null)
                sessionLayer.ClarificationCompleted -= HandleClarificationCompleted;
            DevSignals.FinalQaPhaseRequested -= HandleDevPhaseRequested;
            EndPhaseAndDismiss(notifySession: false);
        }

        public void OnSessionStarted(LobbySessionConfig config)
        {
            _finalQaEnabled = config.FinalQaEnabled;
        }

        public void OnSessionEnded()
        {
            // Drop any open Final Q&A panel so a question never bleeds into
            // a re-launched session or the post-eval lobby. Safe to call
            // repeatedly.
            EndPhaseAndDismiss(notifySession: false);
        }

        public void OnEvaluationStarting() => EndPhaseAndDismiss(notifySession: false);
        public void OnEvaluationCancelled() => EndPhaseAndDismiss(notifySession: false);

        /// <summary>
        /// Entry point invoked by SessionManager.EndSession when Final Q&A is
        /// enabled. Shows the spinner and asks the session module to start
        /// Clarification.
        /// </summary>
        public void BeginFinalQAPhase()
        {
            if (_phaseActive) return;
            if (!_finalQaEnabled)
            {
                // Defensive: SessionManager already routes around us when the
                // toggle is off, but if someone calls this directly skip
                // straight to evaluation.
                if (SessionManager.Instance != null) SessionManager.Instance.BeginEvaluation();
                return;
            }

            _phaseActive = true;
            EnsurePanel();
            PositionPanelInFrontOfPlayer();
            _panel.ShowSpinner();

            sessionLayer?.BeginClarification();
        }

        void HandleClarificationCompleted(ClarificationResult result)
        {
            if (!_phaseActive) return;
            var qs = result?.UnresolvedQuestions;
            if (qs == null || qs.Count == 0)
            {
                // Nothing to ask — drop the spinner and head straight to
                // evaluation. The Final Q&A panel never reaches the question
                // state in this case.
                EndPhaseAndDismiss(notifySession: false);
                if (SessionManager.Instance != null) SessionManager.Instance.BeginEvaluation();
                return;
            }

            _questions = qs;
            _currentIndex = 0;
            ShowCurrentQuestion();
        }

        void ShowCurrentQuestion()
        {
            if (_questions == null) return;
            var q = _questions[_currentIndex];
            bool showNext = _currentIndex < _questions.Count - 1;
            _panel.ShowQuestion(q.Question, _currentIndex + 1, _questions.Count, showNext);

            sessionLayer?.BeginFinalQARound(q);
            _roundOpen = true;
        }

        void HandleNextPressed()
        {
            if (!_phaseActive || _questions == null) return;
            // Next is hidden on the last question — defensive guard for
            // double-fire / programmatic invocation.
            if (_currentIndex >= _questions.Count - 1) return;

            if (_roundOpen)
            {
                sessionLayer?.EndFinalQARound();
                _roundOpen = false;
            }

            _currentIndex++;
            ShowCurrentQuestion();
        }

        void HandleCancelPressed()
        {
            if (!_phaseActive) return;
            CancelDevClarification();
            EndPhaseAndDismiss(notifySession: false);
            if (SessionManager.Instance != null) SessionManager.Instance.BeginEvaluation();
        }

        void HandleFinishPressed()
        {
            if (!_phaseActive) return;
            if (_roundOpen)
            {
                sessionLayer?.EndFinalQARound();
                _roundOpen = false;
            }

            EndPhaseAndDismiss(notifySession: false);
            if (SessionManager.Instance != null) SessionManager.Instance.BeginEvaluation();
        }

        void EndPhaseAndDismiss(bool notifySession)
        {
            if (notifySession && _roundOpen)
                sessionLayer?.EndFinalQARound();

            _roundOpen = false;
            _phaseActive = false;
            _questions = null;
            _currentIndex = 0;

            CancelDevClarification();

            if (_panel != null) _panel.Hide();
        }

        void CancelDevClarification()
        {
            if (_devClarificationRoutine == null) return;
            StopCoroutine(_devClarificationRoutine);
            _devClarificationRoutine = null;
        }

        void EnsurePanel()
        {
            if (_panel != null) return;
            if (panelPrefab == null) return;

            _panel = Instantiate(panelPrefab);
            _panel.gameObject.SetActive(false);

            _panel.OnNextPressed += HandleNextPressed;
            _panel.OnFinishPressed += HandleFinishPressed;
            _panel.OnCancelPressed += HandleCancelPressed;
        }

        void PositionPanelInFrontOfPlayer()
        {
            if (_panel == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            var camPos = cam.transform.position;
            var forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            // Chest-height so the panel sits below the speaker's line of
            // sight; LookAtCamera (yawOnly=false) tilts it up to read.
            var pos = camPos + forward * PanelDistanceMeters;
            pos.y = camPos.y - ChestDropFromEyeMeters;
            _panel.transform.position = pos;
        }

        // -- Dev smoke test ---------------------------------------------------

        void HandleDevPhaseRequested()
        {
            if (_phaseActive) return;

            // Force the gating flag so the dev path works even before a real
            // session has been started.
            _finalQaEnabled = true;
            BeginFinalQAPhase();
            CancelDevClarification();
            _devClarificationRoutine = StartCoroutine(DevFakeClarification());
        }

        IEnumerator DevFakeClarification()
        {
            yield return new WaitForSecondsRealtime(DevClarificationDelaySeconds);
            _devClarificationRoutine = null;
            var sample = new ClarificationResult(new[]
            {
                new FinalQaQuestion(1, "What did you mean by 'sleep consolidates memory'?", ConcernType.ComprehensionGap),
                new FinalQaQuestion(2, "How did you measure that?", ConcernType.DetailRequest),
                new FinalQaQuestion(3, "Could you give a concrete example from the data?", ConcernType.DetailRequest),
            });
            sessionLayer?.RaiseClarificationCompleted(sample);
        }
    }
}
