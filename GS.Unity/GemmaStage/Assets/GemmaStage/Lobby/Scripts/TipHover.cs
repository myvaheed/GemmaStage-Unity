using UnityEngine;
using UnityEngine.EventSystems;

namespace GemmaStage.Lobby
{
    /// <summary>
    /// Lightweight pointer-event proxy. Lives on every raycast target inside a
    /// row (the control plus each label TMP) and forwards Enter/Exit to a
    /// single <see cref="TipHoverGroup"/> on the row root. The group owns the
    /// dwell timer, the anchor, and the tip text — so ray jitter between
    /// siblings of the same row counts as one continuous hover instead of
    /// re-triggering the dwell.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TipHover : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler
    {
        TipHoverGroup group;
        bool groupResolved;

        TipHoverGroup Group
        {
            get
            {
                if (!groupResolved)
                {
                    group = GetComponentInParent<TipHoverGroup>(includeInactive: true);
                    groupResolved = true;
                }
                return group;
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            Group?.NotifyEnter(this);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            Group?.NotifyExit(this);
        }

        void OnDisable()
        {
            Group?.NotifyExit(this);
        }
    }
}
