using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Evaluation
{
    /// <summary>
    /// Pure view for the Evaluation progress floating panel. Owns the
    /// progress bar fill, percentage label, header text, and Cancel button.
    /// The controller drives all session-module calls; this class never
    /// touches the session module directly. Mirrors FinalQaPanelView /
    /// EndSessionPanelView in animation timing and responsibilities.
    /// See PHASE_5_TASKS.md §5.11.1 and GAME_DESIGN.md §8.1.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EvaluationPanelView : MonoBehaviour
    {
        const float ShowAnimationSeconds     = 0.3f;
        const float HideAnimationSeconds     = 0.12f;
        const float ShowStartScale           = 0.5f;
        const float ShowPeakScale            = 1.18f;
        const float ShowPeakAtNormalizedTime = 0.6f;
        const float HideEndScale             = 0.94f;

        [SerializeField] TMP_Text    headerLabel;
        [SerializeField] Image       progressBarFill;
        [SerializeField] TMP_Text    percentageLabel;
        [SerializeField] Button      cancelButton;
        [SerializeField] CanvasGroup canvasGroup;

        public event Action OnCancelPressed;

        Coroutine _animationRoutine;
        Vector3   _baseScale;

        void Awake()
        {
            if (cancelButton != null)
                cancelButton.onClick.AddListener(() => OnCancelPressed?.Invoke());

            _baseScale = transform.localScale;

            // Start hidden and at 0 %.
            if (canvasGroup != null) canvasGroup.alpha = 0f;
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Updates the progress bar fill and percentage label.
        /// t is clamped to [0, 1]. The fill image is anchored-stretched left-to-right
        /// inside the bar container; we drive its right anchor so the 9-sliced rounded
        /// sprite keeps both ends rounded as it grows (vs. Image.Type.Filled which
        /// produces a hard right edge mid-fill).
        /// </summary>
        public void SetProgress(float t)
        {
            t = Mathf.Clamp01(t);
            if (progressBarFill != null)
            {
                var rt = progressBarFill.rectTransform;
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(t, 1f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
            if (percentageLabel != null) percentageLabel.text = $"{Mathf.RoundToInt(t * 100)}%";
        }

        public void Show()
        {
            PlayShowAnimation();
        }

        public void Hide()
        {
            if (!gameObject.activeSelf) return;
            if (_animationRoutine != null) StopCoroutine(_animationRoutine);
            _animationRoutine = StartCoroutine(PlayHideAnimation());
        }

        void PlayShowAnimation()
        {
            gameObject.SetActive(true);
            if (_animationRoutine != null) StopCoroutine(_animationRoutine);
            _animationRoutine = StartCoroutine(ShowAnimationRoutine());
        }

        IEnumerator ShowAnimationRoutine()
        {
            if (canvasGroup != null) canvasGroup.alpha = 0f;
            transform.localScale = _baseScale * ShowStartScale;

            float t = 0f;
            while (t < ShowAnimationSeconds)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / ShowAnimationSeconds);
                if (canvasGroup != null) canvasGroup.alpha = Mathf.Clamp01(k * 3f);

                float scaleK;
                if (k < ShowPeakAtNormalizedTime)
                {
                    float k1    = k / ShowPeakAtNormalizedTime;
                    float eased = 1f - (1f - k1) * (1f - k1) * (1f - k1);
                    scaleK = Mathf.Lerp(ShowStartScale, ShowPeakScale, eased);
                }
                else
                {
                    float k2    = (k - ShowPeakAtNormalizedTime) / (1f - ShowPeakAtNormalizedTime);
                    float eased = k2 < 0.5f
                        ? 2f * k2 * k2
                        : 1f - Mathf.Pow(-2f * k2 + 2f, 2f) * 0.5f;
                    scaleK = Mathf.Lerp(ShowPeakScale, 1f, eased);
                }
                transform.localScale = _baseScale * scaleK;
                yield return null;
            }

            if (canvasGroup != null) canvasGroup.alpha = 1f;
            transform.localScale    = _baseScale;
            _animationRoutine       = null;
        }

        IEnumerator PlayHideAnimation()
        {
            float startAlpha  = canvasGroup != null ? canvasGroup.alpha : 1f;
            float startScaleK = transform.localScale.x / _baseScale.x;

            float t = 0f;
            while (t < HideAnimationSeconds)
            {
                t += Time.unscaledDeltaTime;
                float k     = Mathf.Clamp01(t / HideAnimationSeconds);
                float eased = k * k;
                if (canvasGroup != null) canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, eased);
                transform.localScale = _baseScale * Mathf.Lerp(startScaleK, HideEndScale, eased);
                yield return null;
            }

            if (canvasGroup != null) canvasGroup.alpha = 0f;
            transform.localScale  = _baseScale;
            _animationRoutine     = null;
            gameObject.SetActive(false);
        }
    }
}
