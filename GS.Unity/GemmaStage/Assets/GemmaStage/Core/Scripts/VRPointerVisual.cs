using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GemmaStage.Core
{
    /// <summary>
    /// VR-friendly hover and press feedback for UGUI widgets.
    ///
    /// Why not Selectable.ColorTint:
    ///   XR ray jitter causes PointerEnter/Exit pairs every frame as the controller
    ///   ray oscillates across a Selectable's edge. Selectable.DoStateTransition
    ///   tweens between normalColor/highlightedColor on each pair → visible flicker.
    ///   (Same root cause as VRSlider's OnMove suppression and drag-jitter fix.)
    ///
    /// This component replaces ColorTint with manual state tracking:
    ///   - Hover-enter is DEBOUNCED by hoverDelay (default 80 ms). Transient ray
    ///     drift that enters and exits within the window produces no visual change.
    ///   - Hover-exit, press, and release are always IMMEDIATE so the widget feels
    ///     responsive when the user deliberately moves away or clicks.
    ///   - If an optional Toggle is wired, the "selected" (isOn) state is reflected
    ///     without any pointer involvement — used for dropdown item highlighting.
    ///   - If a Selectable is wired (or auto-resolved), a distinct disabledColor is
    ///     shown whenever interactable == false, and is updated automatically when
    ///     that flag changes at runtime.
    ///
    /// Setup:
    ///   1. Keep Selectable.transition = None on the sibling Button/Toggle/etc.
    ///   2. Add VRPointerVisual to the same GameObject.
    ///   3. Set targetImage (auto-resolved to GetComponent if left null).
    ///   4. Configure normalColor / hoverColor / pressedColor.
    ///   5. To show a distinct disabled look, set trackDisabled = true (auto-resolves
    ///      Selectable from the same GO).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VRPointerVisual : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler,
        IPointerDownHandler, IPointerUpHandler
    {
        [SerializeField] private Image targetImage;
        [SerializeField] private Toggle toggle;

        [Header("Colors")]
        [SerializeField] private Color normalColor   = new Color32(0x7B, 0x61, 0xFF, 0xFF);
        [SerializeField] private Color selectedColor = new Color32(0x7B, 0x61, 0xFF, 0xFF);
        [SerializeField] private Color hoverColor    = new Color32(0x9D, 0x87, 0xFF, 0xFF);
        [SerializeField] private Color pressedColor  = new Color32(0xFF, 0xFF, 0xFF, 0xCC);
        [SerializeField] private Color disabledColor = new Color32(0x35, 0x33, 0x4E, 0xFF);

        [Header("Disabled tracking")]
        [Tooltip("When true, resolves a Selectable from this GameObject and shows disabledColor while interactable == false.")]
        [SerializeField] private bool trackDisabled;

        [Header("Timing")]
        [Tooltip("Seconds the pointer must stay inside before hover activates. Absorbs XR ray jitter without slowing deliberate interaction.")]
        [SerializeField, Range(0f, 0.3f)] private float hoverDelay = 0.08f;

        private bool pointerInside;
        private bool hoverApplied;
        private bool pressed;
        private float enterTime;
        private Selectable selectable;
        private bool wasInteractable = true;

        private void Awake()
        {
            if (targetImage == null) targetImage = GetComponent<Image>();
            if (toggle != null) toggle.onValueChanged.AddListener(OnToggleChanged);
            if (trackDisabled)
            {
                selectable = GetComponent<Selectable>();
                if (selectable != null) wasInteractable = selectable.interactable;
            }
            Refresh();
        }

        private void OnDestroy()
        {
            if (toggle != null) toggle.onValueChanged.RemoveListener(OnToggleChanged);
        }

        private void OnToggleChanged(bool _) => Refresh();

        public void OnPointerEnter(PointerEventData eventData)
        {
            pointerInside = true;
            hoverApplied  = false;
            enterTime     = Time.unscaledTime;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            pointerInside = false;
            hoverApplied  = false;
            if (!pressed) Refresh();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            pressed = true;
            Refresh();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            pressed = false;
            Refresh();
        }

        private void Update()
        {
            if (pointerInside && !hoverApplied && !pressed
                && Time.unscaledTime - enterTime >= hoverDelay)
            {
                hoverApplied = true;
                Refresh();
            }

            // Detect runtime interactable changes (e.g., mic gate enabling the
            // Start Session button) and refresh the tint immediately.
            if (selectable != null && selectable.interactable != wasInteractable)
            {
                wasInteractable = selectable.interactable;
                Refresh();
            }
        }

        private void Refresh()
        {
            if (targetImage == null) return;
            Color c;
            if (selectable != null && !selectable.interactable) c = disabledColor;
            else if (pressed)                                   c = pressedColor;
            else if (hoverApplied)                              c = hoverColor;
            else if (toggle != null && toggle.isOn)             c = selectedColor;
            else                                                c = normalColor;
            targetImage.color = c;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (targetImage == null) targetImage = GetComponent<Image>();
            if (!Application.isPlaying && targetImage != null)
                targetImage.color = normalColor;
        }
#endif
    }
}
