using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.LiveQA
{
    /// <summary>
    /// Pure view for the Live Q&A floating popup. Owns the State_Ask /
    /// State_Answered toggle and surfaces three button events. The controller
    /// drives session-module calls; this class never touches the session
    /// module directly.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LiveQaPanelView : MonoBehaviour
    {
        // The Yes button sits where the Hope button appears in the next state,
        // so a poke landing on Yes can carry through to Hope on the same
        // frame. Lock Hope non-interactable for this long after the swap.
        const float HopeButtonLockoutSeconds = 1f;

        const float ShowAnimationSeconds = 0.3f;
        const float HideAnimationSeconds = 0.12f;
        const float ShowStartScale = 0.5f;
        // Two-phase pop: grow to ShowPeakScale by ShowPeakAtNormalizedTime of
        // the duration, then settle to 1.0. The explicit peak makes the
        // overshoot visible in the headset.
        const float ShowPeakScale = 1.18f;
        const float ShowPeakAtNormalizedTime = 0.6f;
        const float HideEndScale = 0.94f;

        [SerializeField] private GameObject stateAsk;
        [SerializeField] private GameObject stateAnswered;
        [SerializeField] private TMP_Text questionText;
        [SerializeField] private TMP_Text countdownText;
        [SerializeField] private Button yesButton;
        [SerializeField] private Button hopeIAnsweredButton;
        [SerializeField] private CanvasGroup canvasGroup;

        public event Action OnYesPressed;
        public event Action OnHopeIAnsweredPressed;

        Coroutine _hopeUnlockRoutine;
        Coroutine _animationRoutine;
        Vector3 _baseScale;

        private void Awake()
        {
            if (yesButton != null) yesButton.onClick.AddListener(() => OnYesPressed?.Invoke());
            if (hopeIAnsweredButton != null) hopeIAnsweredButton.onClick.AddListener(() => OnHopeIAnsweredPressed?.Invoke());

            // Snapshot prefab scale so show/hide can restore it exactly.
            _baseScale = transform.localScale;
        }

        public void ShowAsk()
        {
            CancelHopeUnlock();
            if (stateAsk != null) stateAsk.SetActive(true);
            if (stateAnswered != null) stateAnswered.SetActive(false);
            PlayShowAnimation();
        }

        public void ShowAnswered(string question)
        {
            if (questionText != null) questionText.text = question ?? string.Empty;
            if (stateAsk != null) stateAsk.SetActive(false);
            if (stateAnswered != null) stateAnswered.SetActive(true);
            PlayShowAnimation();

            if (hopeIAnsweredButton != null)
            {
                hopeIAnsweredButton.interactable = false;
                CancelHopeUnlock();
                _hopeUnlockRoutine = StartCoroutine(UnlockHopeAfterDelay());
            }
        }

        public void Hide()
        {
            CancelHopeUnlock();
            // PlayHideAnimation calls SetActive(false) at the tail so the fade
            // is visible.
            if (!gameObject.activeSelf) return;
            if (_animationRoutine != null) StopCoroutine(_animationRoutine);
            _animationRoutine = StartCoroutine(PlayHideAnimation());
        }

        public void SetCountdownSeconds(int seconds)
        {
            if (countdownText == null) return;
            if (seconds < 0) seconds = 0;
            countdownText.text = $"{seconds}s";
        }

        public void SetCountdownVisible(bool visible)
        {
            if (countdownText == null) return;
            countdownText.gameObject.SetActive(visible);
        }

        IEnumerator UnlockHopeAfterDelay()
        {
            yield return new WaitForSeconds(HopeButtonLockoutSeconds);
            if (hopeIAnsweredButton != null) hopeIAnsweredButton.interactable = true;
            _hopeUnlockRoutine = null;
        }

        void CancelHopeUnlock()
        {
            if (_hopeUnlockRoutine == null) return;
            StopCoroutine(_hopeUnlockRoutine);
            _hopeUnlockRoutine = null;
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
                if (canvasGroup != null) canvasGroup.alpha = Mathf.Clamp01(k * 3f); // alpha finishes before scale

                float scaleK;
                if (k < ShowPeakAtNormalizedTime)
                {
                    float k1 = k / ShowPeakAtNormalizedTime;
                    float eased = 1f - (1f - k1) * (1f - k1) * (1f - k1);
                    scaleK = Mathf.Lerp(ShowStartScale, ShowPeakScale, eased);
                }
                else
                {
                    // Ease-in-out so the peak holds for a perceptible beat
                    // before snapping back to 1.0.
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

        IEnumerator PlayHideAnimation()
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
