using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.EndSession
{
    /// <summary>
    /// Pure view for the End Session confirmation popup. Shape mirrors
    /// LiveQaPanelView's pop-in / fade-out tween for visual consistency. No
    /// state toggle, no countdown — just Yes / No.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EndSessionPanelView : MonoBehaviour
    {
        const float ShowAnimationSeconds = 0.3f;
        const float HideAnimationSeconds = 0.12f;
        const float ShowStartScale = 0.5f;
        const float ShowPeakScale = 1.18f;
        const float ShowPeakAtNormalizedTime = 0.6f;
        const float HideEndScale = 0.94f;

        [SerializeField] Button yesButton;
        [SerializeField] Button noButton;
        [SerializeField] CanvasGroup canvasGroup;

        public event Action OnYesPressed;
        public event Action OnNoPressed;

        Coroutine _animationRoutine;
        Vector3 _baseScale;

        void Awake()
        {
            if (yesButton != null) yesButton.onClick.AddListener(() => OnYesPressed?.Invoke());
            if (noButton != null) noButton.onClick.AddListener(() => OnNoPressed?.Invoke());
            _baseScale = transform.localScale;
        }

        public void Show()
        {
            gameObject.SetActive(true);
            if (_animationRoutine != null) StopCoroutine(_animationRoutine);
            _animationRoutine = StartCoroutine(ShowAnimationRoutine());
        }

        public void Hide()
        {
            if (!gameObject.activeSelf) return;
            if (_animationRoutine != null) StopCoroutine(_animationRoutine);
            _animationRoutine = StartCoroutine(HideAnimationRoutine());
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
                    float k1 = k / ShowPeakAtNormalizedTime;
                    float eased = 1f - (1f - k1) * (1f - k1) * (1f - k1);
                    scaleK = Mathf.Lerp(ShowStartScale, ShowPeakScale, eased);
                }
                else
                {
                    float k2 = (k - ShowPeakAtNormalizedTime) / (1f - ShowPeakAtNormalizedTime);
                    float eased = k2 < 0.5f
                        ? 2f * k2 * k2
                        : 1f - Mathf.Pow(-2f * k2 + 2f, 2f) * 0.5f;
                    scaleK = Mathf.Lerp(ShowPeakScale, 1f, eased);
                }
                transform.localScale = _baseScale * scaleK;
                yield return null;
            }

            if (canvasGroup != null) canvasGroup.alpha = 1f;
            transform.localScale = _baseScale;
            _animationRoutine = null;
        }

        IEnumerator HideAnimationRoutine()
        {
            float startAlpha = canvasGroup != null ? canvasGroup.alpha : 1f;
            float startScaleK = transform.localScale.x / _baseScale.x;

            float t = 0f;
            while (t < HideAnimationSeconds)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / HideAnimationSeconds);
                float eased = k * k;
                if (canvasGroup != null) canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, eased);
                transform.localScale = _baseScale * Mathf.Lerp(startScaleK, HideEndScale, eased);
                yield return null;
            }

            if (canvasGroup != null) canvasGroup.alpha = 0f;
            transform.localScale = _baseScale;
            _animationRoutine = null;
            gameObject.SetActive(false);
        }
    }
}
