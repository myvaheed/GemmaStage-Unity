using System.Collections;
using System.Collections.Generic;
using GemmaStage.Audience;
using GemmaStage.Core;
using GemmaStage.Session;
using UnityEngine;

namespace GemmaStage.LiveQA
{
    /// <summary>
    /// Drives the Live Q&A floating popup during the performance phase.
    /// Gates surfacing on active session + Live Q&A enabled + single-popup
    /// invariant + disturbance probability roll, then orchestrates the
    /// audience hand-raise and panel state transitions.
    /// See docs/SESSION_ARCHITECTURE.md §9.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiveQaController : MonoBehaviour
    {
        const float PopupTimeoutSeconds = 10f;
        // Panel sits above the seated NPC's head. Seat transforms are anchored
        // near the chair floor; a sitting head sits around 1.2m above that, and
        // we want clearance so the panel doesn't overlap the head.
        const float PanelHeightAboveSeatMeters = 1.9f;

        [SerializeField] SessionLayer sessionLayer;
        [SerializeField] AudienceManager audienceManager;
        [Tooltip("World-space LiveQaPanel prefab (Assets/GemmaStage/LiveQA/UI/Prefabs/LiveQaPanel.prefab). Lazy-instantiated on first show, then reused.")]
        [SerializeField] LiveQaPanelView panelPrefab;

        bool _sessionActive;
        bool _liveQaEnabled;
        DisturbanceLevel _disturbance;

        bool _activePopupOpen;
        bool _suppressed;
        int _activeSeatIndex = -1;
        LiveQaConcern _activeConcern;
        Coroutine _timeoutCoroutine;

        LiveQaPanelView _panel;

        void Awake()
        {
            if (sessionLayer == null) sessionLayer = FindAnyObjectByType<SessionLayer>();
            // AudienceManager lives in the env scene; resolve lazily on first use.

            if (sessionLayer == null) Debug.LogError("[LiveQaController] SessionLayer missing.");
            if (panelPrefab == null) Debug.LogError("[LiveQaController] LiveQaPanel prefab not assigned.");
        }

        AudienceManager ResolveAudienceManager()
        {
            if (audienceManager == null) audienceManager = FindAnyObjectByType<AudienceManager>();
            return audienceManager;
        }

        void OnEnable()
        {
            if (sessionLayer != null)
                sessionLayer.OpenConcernsUpdated += HandleOpenConcernsUpdated;
            DevSignals.LiveQaPopupRequested += HandleDevPopupRequested;
        }

        void OnDisable()
        {
            if (sessionLayer != null)
                sessionLayer.OpenConcernsUpdated -= HandleOpenConcernsUpdated;
            DevSignals.LiveQaPopupRequested -= HandleDevPopupRequested;
            DismissPopup(notifyAudience: false);
        }

        void HandleDevPopupRequested() => DevForceShow();

        public void OnSessionStarted(LobbySessionConfig config)
        {
            _sessionActive = true;
            _liveQaEnabled = config.LiveQaEnabled;
            _disturbance = config.Disturbance;
        }

        // Drop any open popup so a question never bleeds into the
        // post-performance UI.
        public void OnSessionEnded()
        {
            _sessionActive = false;
            DismissPopup(notifyAudience: true);
        }

        // EndSessionController calls SetSuppressed(true) while the End Session
        // confirmation panel is visible: dismiss any open Live Q&A popup and gate
        // new OpenConcernsUpdated rolls. SetSuppressed(false) on No re-enables.
        // Yes never resumes — OnSessionEnded supersedes when SessionManager.EndSession runs.
        public void SetSuppressed(bool suppressed)
        {
            _suppressed = suppressed;
            if (suppressed) DismissPopup(notifyAudience: true);
        }

        void HandleOpenConcernsUpdated(IReadOnlyList<LiveQaConcern> openSet)
        {
            if (!_sessionActive) return;
            if (!_liveQaEnabled) return;
            if (_activePopupOpen) return;
            if (_suppressed) return;
            if (openSet == null || openSet.Count == 0) return;

            if (Random.value >= _disturbance.ToProbability()) return;

            var concern = openSet[Random.Range(0, openSet.Count)];
            var audience = ResolveAudienceManager();
            var seat = audience != null ? audience.RandomSeatIndex() : -1;
            // Skip the round if no NPC is seated rather than show a popup with
            // no hand-raise.
            if (seat < 0) return;

            BeginPopup(audience, seat, concern);
        }

        // Dev-only smoke test path: bypasses the gating in
        // HandleOpenConcernsUpdated but still respects the single-popup invariant.
        public void DevForceShow(string sampleQuestion = "What did you mean by that?")
        {
            if (_activePopupOpen) return;

            var audience = ResolveAudienceManager();
            var seat = audience != null ? audience.RandomSeatIndex() : -1;
            if (seat < 0)
            {
                Debug.LogWarning("[LiveQaController] DevForceShow: no audience seated; cannot surface popup.");
                return;
            }

            var fake = new LiveQaConcern(id: -1, question: sampleQuestion, type: ConcernType.ComprehensionGap);
            BeginPopup(audience, seat, fake);
        }

        void BeginPopup(AudienceManager audience, int seat, LiveQaConcern concern)
        {
            _activePopupOpen = true;
            _activeSeatIndex = seat;
            _activeConcern = concern;

            audience.PlayReaction(seat, AudienceReaction.HandRaise);
            // Lock so ReturnToIdleBehaviour can't drop the hand before the
            // popup resolves.
            audience.SetReactionLocked(seat, true);

            EnsurePanel();
            PositionPanelAboveSeat(audience, seat);
            _panel.ShowAsk();

            _timeoutCoroutine = StartCoroutine(PopupTimeoutRoutine());
        }

        IEnumerator PopupTimeoutRoutine()
        {
            // WaitForSecondsRealtime so a paused Time.timeScale can't freeze
            // the user out of the popup.
            int remaining = Mathf.CeilToInt(PopupTimeoutSeconds);
            EnsurePanel();
            _panel.SetCountdownVisible(true);
            while (remaining > 0)
            {
                _panel.SetCountdownSeconds(remaining);
                yield return new WaitForSecondsRealtime(1f);
                remaining--;
            }
            _panel.SetCountdownSeconds(0);
            _timeoutCoroutine = null;
            DismissPopup(notifyAudience: true);
        }

        void HandleYesPressed()
        {
            if (!_activePopupOpen) return;
            CancelTimeout();
            // Countdown belongs to the Ask state only — once accepted, no
            // more pressure.
            _panel?.SetCountdownVisible(false);

            sessionLayer?.BeginOnLiveQARound(_activeConcern);
            _panel.ShowAnswered(_activeConcern.Question);
        }

        void HandleHopeAnsweredPressed()
        {
            if (!_activePopupOpen) return;
            sessionLayer?.EndOnLiveQARound();
            DismissPopup(notifyAudience: true);
        }

        // notifyAudience=false during teardown (OnDisable, scene unload), where
        // AudienceManager may already be gone.
        void DismissPopup(bool notifyAudience)
        {
            CancelTimeout();

            if (notifyAudience && _activeSeatIndex >= 0 && audienceManager != null)
            {
                audienceManager.SetReactionLocked(_activeSeatIndex, false);
                audienceManager.ResetReaction(_activeSeatIndex);
            }

            if (_panel != null) _panel.Hide();

            _activePopupOpen = false;
            _activeSeatIndex = -1;
            _activeConcern = null;
        }

        void CancelTimeout()
        {
            if (_timeoutCoroutine == null) return;
            StopCoroutine(_timeoutCoroutine);
            _timeoutCoroutine = null;
        }

        void EnsurePanel()
        {
            if (_panel != null) return;
            if (panelPrefab == null) return;

            _panel = Instantiate(panelPrefab);
            _panel.gameObject.SetActive(false);

            _panel.OnYesPressed += HandleYesPressed;
            _panel.OnHopeIAnsweredPressed += HandleHopeAnsweredPressed;
        }

        void PositionPanelAboveSeat(AudienceManager audience, int seat)
        {
            if (_panel == null) return;
            var seatTransform = audience != null ? audience.GetSeatTransform(seat) : null;
            if (seatTransform == null) return;

            // LookAtCamera on the panel handles facing; we just place it
            // above the seat anchor at head + clearance height.
            var pos = seatTransform.position;
            pos.y += PanelHeightAboveSeatMeters;
            _panel.transform.position = pos;
        }
    }
}
