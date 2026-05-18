using System;
using GemmaStage.Core;
using GemmaStage.LiveQA;
using GemmaStage.Session;
using UnityEngine;
using UnityEngine.InputSystem;

namespace GemmaStage.EndSession
{
    /// <summary>
    /// Owns the End Session confirmation popup. Subscribes to B/Y (upper face
    /// buttons) only while the rig is in Performance mode. Show/Hide pushes
    /// and pops a far-panel hold on RayController, suppresses Live Q&A, and
    /// pauses Perceptor (logical pause — mic stays open).
    /// See PHASE_5_TASKS.md §5.9 + GAME_DESIGN.md §7.1.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EndSessionController : MonoBehaviour
    {
        // Speaking-distance far panel (per PHASE_5_TASKS.md §5.9.2). Eye-level —
        // unlike the Live Q&A chest-height popup, this one is meant to be read
        // and clicked via the active-hand ray, not poked.
        const float PanelDistanceMeters = 1.5f;

        [SerializeField] InputActionReference leftEndSessionAction;
        [SerializeField] InputActionReference rightEndSessionAction;

        [SerializeField] RayController rayController;
        [SerializeField] SessionManager sessionManager;
        [SerializeField] SessionLayer sessionLayer;
        [SerializeField] LiveQaController liveQaController;
        [SerializeField] WristTimer wristTimer;

        [Tooltip("World-space EndSessionPanel prefab. Lazy-instantiated on first show, then reused.")]
        [SerializeField] EndSessionPanelView panelPrefab;

        EndSessionPanelView _panel;
        bool _panelOpen;
        bool _farHoldActive;
        bool _leftBound, _rightBound;

        void Awake()
        {
            if (rayController == null) rayController = FindAnyObjectByType<RayController>();
            if (sessionManager == null) sessionManager = FindAnyObjectByType<SessionManager>();
            if (sessionLayer == null) sessionLayer = FindAnyObjectByType<SessionLayer>();
            if (liveQaController == null) liveQaController = FindAnyObjectByType<LiveQaController>();
            if (wristTimer == null) wristTimer = FindAnyObjectByType<WristTimer>();

            if (rayController == null) Debug.LogError("[EndSessionController] RayController missing.");
            if (sessionManager == null) Debug.LogError("[EndSessionController] SessionManager missing.");
            if (panelPrefab == null) Debug.LogError("[EndSessionController] EndSessionPanel prefab not assigned.");
        }

        void OnEnable()
        {
            DevSignals.EndSessionPopupRequested += HandleDevPopupRequested;
            if (rayController != null)
            {
                rayController.OnModeChanged += HandleModeChanged;
                HandleModeChanged(rayController.CurrentMode);
            }
        }

        void OnDisable()
        {
            DevSignals.EndSessionPopupRequested -= HandleDevPopupRequested;
            if (rayController != null) rayController.OnModeChanged -= HandleModeChanged;
            // Defensive cleanup in case the rig is torn down mid-popup.
            if (_panelOpen) TeardownLikeNo();
            UnbindActions();
        }

        void HandleModeChanged(RayMode mode)
        {
            if (mode == RayMode.Performance) BindActions();
            else
            {
                UnbindActions();
                if (_panelOpen) TeardownLikeNo();
            }
        }

        void OnEndSessionPressed(InputAction.CallbackContext _)
        {
            // Toggle behaviour: pressing the upper button while the panel is
            // already visible dismisses it (same path as No). Otherwise gate on
            // an active live phase — once Yes was pressed, _sessionActive flips
            // to false and the panel must not re-appear during Final Q&A /
            // Evaluation.
            if (_panelOpen) { TeardownLikeNo(); return; }
            if (sessionManager != null && !sessionManager.SessionActive) return;
            ShowPanel();
        }

        void HandleDevPopupRequested()
        {
            // Dev hook stays ungated by SessionActive so the popup is reachable
            // for smoke-testing without a real session, but it still toggles.
            if (_panelOpen) { TeardownLikeNo(); return; }
            ShowPanel();
        }

        void ShowPanel()
        {
            EnsurePanel();
            PositionPanelInFrontOfPlayer();

            if (rayController != null)
            {
                rayController.PushFarPanelHold();
                _farHoldActive = true;
            }
            liveQaController?.SetSuppressed(true);
            sessionLayer?.PauseLivePhase();
            wristTimer?.Pause();

            _panel.Show();
            _panelOpen = true;
        }

        void HandleYesPressed()
        {
            if (!_panelOpen) return;
            _panelOpen = false;

            ReleaseFarHold();
            _panel.Hide();

            // Note: do NOT call ResumeLivePhase — SessionLayer.End() supersedes
            // the pause, and Live Q&A is dismissed by SessionManager.EndSession
            // via _liveQaController.OnSessionEnded().
            sessionManager?.EndSession();
        }

        void HandleNoPressed()
        {
            if (!_panelOpen) return;
            TeardownLikeNo();
        }

        void TeardownLikeNo()
        {
            _panelOpen = false;
            ReleaseFarHold();
            liveQaController?.SetSuppressed(false);
            sessionLayer?.ResumeLivePhase();
            wristTimer?.Resume();
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

            _panel.OnYesPressed += HandleYesPressed;
            _panel.OnNoPressed += HandleNoPressed;
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

            // Eye-level placement; LookAtCamera on the prefab keeps it normal.
            var pos = camPos + forward * PanelDistanceMeters;
            pos.y = camPos.y;
            _panel.transform.position = pos;
        }

        void BindActions()
        {
            BindAction(leftEndSessionAction, ref _leftBound, OnEndSessionPressed);
            BindAction(rightEndSessionAction, ref _rightBound, OnEndSessionPressed);
        }

        void UnbindActions()
        {
            UnbindAction(leftEndSessionAction, ref _leftBound, OnEndSessionPressed);
            UnbindAction(rightEndSessionAction, ref _rightBound, OnEndSessionPressed);
        }

        static void BindAction(InputActionReference r, ref bool flag, Action<InputAction.CallbackContext> handler)
        {
            if (flag || r == null || r.action == null) return;
            r.action.performed += handler;
            r.action.Enable();
            flag = true;
        }

        static void UnbindAction(InputActionReference r, ref bool flag, Action<InputAction.CallbackContext> handler)
        {
            if (!flag || r == null || r.action == null) return;
            r.action.performed -= handler;
            flag = false;
        }
    }
}
