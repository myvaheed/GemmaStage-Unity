using GemmaStage.Core;
using UnityEngine;

namespace GemmaStage.Session
{
    // Surfaces SessionLayer.SessionFailed as a "Something went wrong" floating
    // panel. Lives in Shared.unity alongside SessionManager/SessionLayer so the
    // subscription survives every Lobby↔Env scene swap.
    //
    // OK routes to SessionManager.CancelEvaluation() which is the universal
    // teardown path — disposes the native session, releases the mic, returns
    // the player to the Lobby, and switches the ray back to Lobby mode.
    [DisallowMultipleComponent]
    public sealed class SessionFailedController : MonoBehaviour
    {
        const float PanelDistanceMeters = 1.5f;

        [SerializeField] SessionLayer            sessionLayer;
        [SerializeField] SessionManager          sessionManager;
        [SerializeField] RayController           rayController;
        [SerializeField] SessionFailedPanelView  panelPrefab;

        SessionFailedPanelView _panel;
        bool _panelOpen;
        bool _farHoldActive;

        void Awake()
        {
            if (sessionLayer   == null) sessionLayer   = FindAnyObjectByType<SessionLayer>();
            if (sessionManager == null) sessionManager = FindAnyObjectByType<SessionManager>();
            if (rayController  == null) rayController  = FindAnyObjectByType<RayController>();

            if (sessionLayer   == null) Debug.LogError("[SessionFailedController] SessionLayer missing.");
            if (sessionManager == null) Debug.LogError("[SessionFailedController] SessionManager missing.");
            if (panelPrefab    == null) Debug.LogError("[SessionFailedController] SessionFailedPanel prefab not assigned.");
        }

        void OnEnable()
        {
            if (sessionLayer != null)
                sessionLayer.SessionFailed += HandleSessionFailed;
        }

        void OnDisable()
        {
            if (sessionLayer != null)
                sessionLayer.SessionFailed -= HandleSessionFailed;
            if (_panelOpen) TeardownPanel();
        }

        void HandleSessionFailed(string role, string message)
        {
            // Idempotent — the module may emit multiple SessionFailed events if
            // several stages error in quick succession. Keep the first message.
            if (_panelOpen) return;

            EnsurePanel();
            if (_panel == null) return;

            PositionPanelInFrontOfPlayer();
            if (rayController != null)
            {
                rayController.PushFarPanelHold();
                _farHoldActive = true;
            }

            _panel.Show(role, message);
            _panelOpen = true;
        }

        void HandleOkPressed()
        {
            if (!_panelOpen) return;
            TeardownPanel();
            // Universal teardown — disposes the session, returns to Lobby,
            // resets ray mode.
            sessionManager?.CancelEvaluation();
        }

        void TeardownPanel()
        {
            _panelOpen = false;
            if (_farHoldActive)
            {
                _farHoldActive = false;
                rayController?.PopFarPanelHold();
            }
            if (_panel != null) _panel.Hide();
        }

        void EnsurePanel()
        {
            if (_panel != null) return;
            if (panelPrefab == null) return;
            _panel = Instantiate(panelPrefab);
            _panel.gameObject.SetActive(false);
            _panel.OnOkPressed += HandleOkPressed;
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

            var pos = camPos + forward * PanelDistanceMeters;
            pos.y = camPos.y;
            _panel.transform.position = pos;
        }
    }
}
