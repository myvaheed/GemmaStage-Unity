using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.FinalQA
{
    /// <summary>
    /// Pure view for the Final Q&A floating popup. Owns the State_Spinner /
    /// State_Question toggle and surfaces two button events. The controller
    /// drives session-module calls; this class never touches the session
    /// module directly. Mirrors LiveQaPanelView in animation timing and
    /// responsibilities.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FinalQaPanelView : MonoBehaviour
    {
        const float ShowAnimationSeconds = 0.3f;
        const float HideAnimationSeconds = 0.12f;
        const float ShowStartScale = 0.5f;
        const float ShowPeakScale = 1.18f;
        const float ShowPeakAtNormalizedTime = 0.6f;
        const float HideEndScale = 0.94f;

        // The Finish button sits where Next does in the previous round, so a
        // poke landing on Next can carry through to Finish on the same frame.
        // Lock Finish non-interactable for this long after a state swap.
        const float FinishButtonLockoutSeconds = 1f;

        const float SpinnerRotationPeriodSeconds = 1.5f;

        [SerializeField] private GameObject stateSpinner;
        [SerializeField] private GameObject stateQuestion;
        [SerializeField] private TMP_Text questionText;
        [SerializeField] private TMP_Text counterText;
        [SerializeField] private Button nextButton;
        [SerializeField] private Button finishButton;
        [SerializeField] private Button cancelButton;
        [SerializeField] private Transform spinnerRotor;
        [SerializeField] private CanvasGroup canvasGroup;

        public event Action OnNextPressed;
        public event Action OnFinishPressed;
        public event Action OnCancelPressed;

        Coroutine _animationRoutine;
        Coroutine _finishUnlockRoutine;
        Vector3 _baseScale;

        private void Awake()
        {
            if (nextButton != null) nextButton.onClick.AddListener(() => OnNextPressed?.Invoke());
            if (finishButton != null) finishButton.onClick.AddListener(() => OnFinishPressed?.Invoke());
            if (cancelButton != null) cancelButton.onClick.AddListener(() => OnCancelPressed?.Invoke());

            _baseScale = transform.localScale;
        }

        private void Update()
        {
            // Idle spinner rotation. Unscaled so a paused Time.timeScale can't
            // freeze the indicator.
            if (spinnerRotor == null) return;
            if (stateSpinner == null || !stateSpinner.activeInHierarchy) return;
            spinnerRotor.Rotate(0f, 0f, -360f * Time.unscaledDeltaTime / SpinnerRotationPeriodSeconds);
        }

        public void ShowSpinner()
        {
            CancelFinishUnlock();
            if (stateSpinner != null) stateSpinner.SetActive(true);
            if (stateQuestion != null) stateQuestion.SetActive(false);
            PlayShowAnimation();
        }

        public void ShowQuestion(string text, int oneBased, int total, bool showNext)
        {
            if (questionText != null) questionText.text = text ?? string.Empty;
            if (counterText != null) counterText.text = $"{oneBased}/{total}";
            if (nextButton != null) nextButton.gameObject.SetActive(showNext);

            if (stateSpinner != null) stateSpinner.SetActive(false);
            if (stateQuestion != null) stateQuestion.SetActive(true);
            PlayShowAnimation();

            // Brief lockout so an in-flight Next-poke doesn't carry through to
            // Finish on the same frame after a state swap.
            if (finishButton != null)
            {
                finishButton.interactable = false;
                CancelFinishUnlock();
                _finishUnlockRoutine = StartCoroutine(UnlockFinishAfterDelay());
            }
        }

        public void Hide()
        {
            CancelFinishUnlock();
            if (!gameObject.activeSelf) return;
            if (_animationRoutine != null) StopCoroutine(_animationRoutine);
            _animationRoutine = StartCoroutine(PlayHideAnimation());
        }

        IEnumerator UnlockFinishAfterDelay()
        {
            yield return new WaitForSeconds(FinishButtonLockoutSeconds);
            if (finishButton != null) finishButton.interactable = true;
            _finishUnlockRoutine = null;
        }

        void CancelFinishUnlock()
        {
            if (_finishUnlockRoutine == null) return;
            StopCoroutine(_finishUnlockRoutine);
            _finishUnlockRoutine = null;
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
