using System;
using System.IO;
using GemmaStage.Core;
using GemmaStage.Session;
using UnityEngine;

namespace GemmaStage.Results
{
    // Bridges the SessionLayer.SessionResultsAvailable event to the Results
    // floating panel. Lazy-instantiates the panel prefab, positions it in
    // front of the player, and handles the two footer actions: Save as PDF
    // (opens the PDF report that SessionLayer auto-wrote at session end) and
    // Go to Lobby (delegates to SessionManager).
    //
    // Lives in Shared.unity alongside SessionManager.
    [DisallowMultipleComponent]
    public sealed class ResultsController : MonoBehaviour
    {
        // Far-panel distance matches EvaluationProgressController.
        const float PanelDistanceMeters = 1.5f;

        [SerializeField] ResultsPanelView panelPrefab;
        [SerializeField] RayController    rayController;
        [SerializeField] SessionManager   sessionManager;
        [SerializeField] SessionLayer     sessionLayer;

        [Tooltip("Subdirectory under Application.persistentDataPath where session XLSX/PDF exports live. Must match SessionLayer._xlsxOutputDir.")]
        [SerializeField] string exportsSubDir = "SessionExports";

        ResultsPanelView _panel;
        bool             _farHoldActive;

        void Awake()
        {
            if (rayController  == null) rayController  = FindAnyObjectByType<RayController>();
            if (sessionManager == null) sessionManager = FindAnyObjectByType<SessionManager>();
            if (sessionLayer   == null) sessionLayer   = FindAnyObjectByType<SessionLayer>();

            if (panelPrefab    == null) Debug.LogError("[ResultsController] Panel prefab not assigned.");
            if (rayController  == null) Debug.LogWarning("[ResultsController] RayController not found.");
            if (sessionManager == null) Debug.LogWarning("[ResultsController] SessionManager not found.");
            if (sessionLayer   == null) Debug.LogWarning("[ResultsController] SessionLayer not found — Save-as-PDF will fall back to the exports folder.");
        }

        // ── Entry point (called by EvaluationProgressController) ─────────────

        public void ShowResults(SessionResultsPayload payload)
        {
            EnsurePanel();
            if (_panel == null) return;

            _panel.Populate(payload);
            PositionPanelInFrontOfPlayer();
            PushFarHold();
            _panel.Show();
        }

        // ── Internal ──────────────────────────────────────────────────────────

        void EnsurePanel()
        {
            if (_panel != null) return;
            if (panelPrefab == null) return;

            _panel = Instantiate(panelPrefab);
            _panel.gameObject.SetActive(false);
            _panel.OnSavePdfPressed  += HandleSavePdf;
            _panel.OnGoToLobbyPressed += HandleGoToLobby;
        }

        void PositionPanelInFrontOfPlayer()
        {
            if (_panel == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            var forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            var pos = cam.transform.position + forward * PanelDistanceMeters;
            pos.y = cam.transform.position.y;
            _panel.transform.position = pos;
            _panel.transform.rotation = Quaternion.LookRotation(forward);
        }

        void HandleSavePdf()
        {
            // SessionLayer auto-writes a `session-<ts>.pdf` next to the xlsx
            // at the end of the post-performance pipeline. Open that file
            // directly so the player lands in their system PDF viewer instead
            // of having to fish the file out of the exports folder.
            var pdfPath = sessionLayer != null ? sessionLayer.LastExportedPdfPath : null;

            if (!string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath))
            {
                Debug.Log($"[ResultsController] Opening PDF: {pdfPath}");
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(pdfPath) { UseShellExecute = true };
                    System.Diagnostics.Process.Start(psi);
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ResultsController] Failed to open PDF directly ({ex.Message}); falling back to exports folder.");
                }
            }
            else
            {
                Debug.LogWarning($"[ResultsController] PDF report unavailable (path='{pdfPath ?? "<null>"}'); opening exports folder instead.");
            }

            // Fallback: reveal the exports folder so the player can still find
            // whatever did get written (xlsx, partial PDF, …).
            var dir = Path.Combine(Application.persistentDataPath, exportsSubDir);
            if (Directory.Exists(dir))
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                System.Diagnostics.Process.Start("explorer.exe", dir);
#endif
            }
            else
            {
                Debug.LogWarning($"[ResultsController] Exports folder not found: {dir}");
            }
        }

        void HandleGoToLobby()
        {
            TeardownPanel();
            sessionManager?.CancelEvaluation();
        }

        void TeardownPanel()
        {
            PopFarHold();
            if (_panel != null) _panel.Hide();
        }

        void PushFarHold()
        {
            if (_farHoldActive) return;
            _farHoldActive = true;
            rayController?.PushFarPanelHold();
        }

        void PopFarHold()
        {
            if (!_farHoldActive) return;
            _farHoldActive = false;
            rayController?.PopFarPanelHold();
        }
    }
}
