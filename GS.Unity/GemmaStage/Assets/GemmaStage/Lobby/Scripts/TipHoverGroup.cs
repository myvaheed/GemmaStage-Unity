using System.Collections;
using UnityEngine;

namespace GemmaStage.Lobby
{
    /// <summary>
    /// Row-level hover aggregator. Sibling <see cref="TipHover"/> proxies on
    /// the row's controls and label TMPs forward Enter/Exit here, so any
    /// pointer movement that stays inside the row counts as one continuous
    /// hover. The group owns the dwell timer, the popup anchor (defaults to
    /// the row's transform), and the tip copy.
    ///
    /// Coroutine ownership: the dwell timer runs on this GameObject (which is
    /// active while the user is actually hovering it). The exit-grace timer
    /// is hosted by <see cref="TipPopupController"/> instead, because the
    /// row's GameObject can become inactive mid-frame during a parent's
    /// SetActive(false) cascade — Unity then refuses to start a coroutine on
    /// the row even when the guard check a few lines earlier reported the
    /// component as still active. The controller's GameObject is always
    /// active, so it's a safe host for the grace coroutine.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TipHoverGroup : MonoBehaviour
    {
        const float DefaultHoverDelaySeconds = 1.0f;
        const float DefaultExitGraceSeconds  = 0.08f;

        [SerializeField, TextArea(2, 5)] string tipText;
        [Tooltip("Optional. World-space transform the popup anchors above. Defaults to this transform (the row root).")]
        [SerializeField] Transform tipAnchor;
        [SerializeField, Range(0f, 5f)] float hoverDelaySeconds = DefaultHoverDelaySeconds;
        [SerializeField, Range(0f, 0.5f)] float exitGraceSeconds = DefaultExitGraceSeconds;

        int activeCount;
        Coroutine dwellRoutine;

        public Transform AnchorTransform => tipAnchor != null ? tipAnchor : transform;

        public void SetText(string text)
        {
            tipText = text;
            var controller = TipPopupController.Instance;
            if (controller != null && controller.IsShowingFor(this))
                controller.Show(this, tipText, AnchorTransform);
        }

        public void NotifyEnter(TipHover proxy)
        {
            activeCount++;

            // Re-entered while the controller's grace coroutine was pending →
            // cancel the scheduled hide so the popup stays visible.
            TipPopupController.Instance?.CancelPendingHide(this);

            if (dwellRoutine == null
                && !string.IsNullOrEmpty(tipText)
                && isActiveAndEnabled
                && TipPopupController.Instance?.IsShowingFor(this) != true)
            {
                dwellRoutine = StartCoroutine(DwellThenShow());
            }
        }

        public void NotifyExit(TipHover proxy)
        {
            if (activeCount > 0) activeCount--;
            if (activeCount > 0) return;

            CancelDwell();

            // Always delegate to the controller — never start a coroutine on
            // this row. During PresentationPickerPopup.SetSiblingsHidden the
            // row is being deactivated and StartCoroutine would log an error.
            TipPopupController.Instance?.RequestDelayedHide(this, exitGraceSeconds);
        }

        void OnDisable()
        {
            activeCount = 0;
            CancelDwell();
            var controller = TipPopupController.Instance;
            if (controller != null)
            {
                controller.CancelPendingHide(this);
                controller.Hide(this);
            }
        }

        IEnumerator DwellThenShow()
        {
            float t = 0f;
            while (t < hoverDelaySeconds)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            dwellRoutine = null;
            if (activeCount > 0)
                TipPopupController.Instance?.Show(this, tipText, AnchorTransform);
        }

        void CancelDwell()
        {
            if (dwellRoutine != null)
            {
                StopCoroutine(dwellRoutine);
                dwellRoutine = null;
            }
        }
    }
}
