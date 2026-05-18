using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Core
{
    // Pure view for the full-screen blackout loading overlay shown during the
    // Setup → Stage transition. Cancel button is intentionally absent — per the
    // GS-222 design decision the user can only cancel once the env is fully
    // loaded (the existing End Session flow takes over from there).
    //
    // Constructed by LoadingOverlayPanelBuilder; positioned in front of the
    // active camera by LoadingOverlayController.
    [DisallowMultipleComponent]
    public sealed class LoadingOverlayPanelView : MonoBehaviour
    {
        const float SpinnerDegreesPerSecond = 240f;

        [SerializeField] CanvasGroup canvasGroup;
        [Tooltip("RectTransform of the spinner Image; rotated each Update by SpinnerDegreesPerSecond around its Z axis.")]
        [SerializeField] RectTransform spinner;

        void Awake()
        {
            if (canvasGroup != null) canvasGroup.alpha = 0f;
            gameObject.SetActive(false);
        }

        void Update()
        {
            if (spinner == null) return;
            // Negative Z so it rotates clockwise from the player's POV.
            spinner.Rotate(0f, 0f, -SpinnerDegreesPerSecond * Time.unscaledDeltaTime);
        }

        public void Show()
        {
            gameObject.SetActive(true);
            if (canvasGroup != null) canvasGroup.alpha = 1f;
        }

        public void Hide()
        {
            if (canvasGroup != null) canvasGroup.alpha = 0f;
            gameObject.SetActive(false);
        }
    }
}
