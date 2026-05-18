using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Core
{
    /// <summary>
    /// Helpers that suppress XR-ray–driven color flicker on UGUI Selectables.
    ///
    /// Why:
    ///   XR ray UI uses TrackedDeviceEventData. The cursor position is rebuilt every
    ///   frame from the controller ray's hit point, which jitters as the headset/hand
    ///   moves. When the ray drifts in and out of a Selectable's RectTransform across
    ///   adjacent frames, GraphicRaycaster fires PointerEnter / PointerExit pairs in
    ///   rapid succession, and Selectable.DoStateTransition tweens the targetGraphic
    ///   between normalColor and highlightedColor on each pair. The result is visible
    ///   color flicker on buttons, dropdown items, and toggle highlights.
    ///
    ///   This is the same root cause as the perpetual +/-1 step in stock
    ///   <see cref="UnityEngine.UI.Slider"/> (see <c>GemmaStage.Lobby.VRSlider</c>): VR
    ///   ray UI is not a desktop pointer, and Selectable's transition machinery wasn't
    ///   designed for sub-frame hit-test churn.
    ///
    /// Fix:
    ///   Set Selectable.transition = None for VR-facing widgets. Hover feedback is
    ///   sacrificed (tolerable in VR — the ray cursor itself indicates aim), but the
    ///   widget stays visually stable. "Selected" / "checked" state is still expressed
    ///   via Toggle.graphic alpha (the per-item white check on the chosen dropdown
    ///   value, the purple fill on a toggle's CheckedVisuals, etc.).
    /// </summary>
    public static class VRSelectableExtensions
    {
        public static void DisableColorTintFlicker(this Selectable s)
        {
            if (s == null) return;
            s.transition = Selectable.Transition.None;
        }
    }
}
