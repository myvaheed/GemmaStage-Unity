using GemmaStage.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Audience
{
    public class DevAudiencePanel : MonoBehaviour
    {
        [SerializeField] Button raiseHandButton;
        [SerializeField] Button clearAllButton;
        [SerializeField] Button startFinalQaButton;
        [SerializeField] TMP_Text activeHandLabel;

        [Header("Scene references (wire in Inspector or resolved on enable)")]
        [SerializeField] AudienceManager audienceManager;
        [SerializeField] ActiveHandController activeHandController;

        void OnEnable()
        {
            if (audienceManager == null)
            {
                audienceManager = FindAnyObjectByType<AudienceManager>();
                if (audienceManager == null)
                    Debug.LogWarning("[DevAudiencePanel] AudienceManager not found — wire the reference in the Inspector.");
            }

            if (activeHandController == null)
            {
                activeHandController = FindAnyObjectByType<ActiveHandController>();
                if (activeHandController == null)
                    Debug.LogWarning("[DevAudiencePanel] ActiveHandController not found — wire the reference in the Inspector.");
            }

            if (raiseHandButton != null) raiseHandButton.onClick.AddListener(OnRaiseHandClicked);
            if (clearAllButton != null) clearAllButton.onClick.AddListener(OnClearAllClicked);
            if (startFinalQaButton != null) startFinalQaButton.onClick.AddListener(OnStartFinalQaClicked);
        }

        void OnDisable()
        {
            if (raiseHandButton != null) raiseHandButton.onClick.RemoveListener(OnRaiseHandClicked);
            if (clearAllButton != null) clearAllButton.onClick.RemoveListener(OnClearAllClicked);
            if (startFinalQaButton != null) startFinalQaButton.onClick.RemoveListener(OnStartFinalQaClicked);
        }

        void Update()
        {
            if (activeHandLabel == null) return;
            var hand = activeHandController != null ? activeHandController.ActiveHand : Hand.None;
            activeHandLabel.text = $"Active hand: {hand}";
        }

        void OnRaiseHandClicked()
        {
            // Route through DevSignals → LiveQaController.DevForceShow, which
            // owns the full smoke-test flow (audience seat pick, hand-raise
            // reaction, popup surface). LiveQaController lives in Shared,
            // which is always loaded, so the listener is always present in
            // normal play.
            DevSignals.RequestLiveQaPopup();
        }

        void OnClearAllClicked()
        {
            if (audienceManager == null) return;
            audienceManager.ClearAllReactions();
        }

        void OnStartFinalQaClicked()
        {
            // Route through DevSignals → FinalQaController.HandleDevPhaseRequested,
            // which shows the spinner and synthesises a ClarificationCompleted
            // event with sample questions after a short delay.
            DevSignals.RequestFinalQaPhase();
        }
    }
}
