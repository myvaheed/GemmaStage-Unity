using UnityEngine;
using UnityEngine.UI;
using GemmaStage.Core;

namespace GemmaStage.Lobby
{
    /// <summary>
    /// Recolors a Slider's fill and handle to match the design-system disabled palette
    /// when <see cref="Slider.interactable"/> is false. Unity's Selectable color-tint
    /// only reaches the targetGraphic (the track Background) — fill and handle stay at
    /// their authoring color, which produces a half-disabled look (gray track, purple
    /// fill). This script syncs all three so disabled sliders read as fully gray.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Slider))]
    public sealed class SliderInteractableTint : MonoBehaviour
    {
        [SerializeField] private Image fillImage;
        [SerializeField] private Image handleImage;
        [SerializeField] private CanvasGroup canvasGroup;

        private Slider slider;
        private bool? lastState;

        private void Awake()
        {
            slider = GetComponent<Slider>();
            if (fillImage == null && slider.fillRect != null) fillImage = slider.fillRect.GetComponent<Image>();
            if (handleImage == null && slider.handleRect != null) handleImage = slider.handleRect.GetComponent<Image>();
            if (canvasGroup == null) canvasGroup = GetComponent<CanvasGroup>();
            Apply(slider.interactable);
        }

        private void Update()
        {
            if (slider == null) return;
            if (!lastState.HasValue || lastState.Value != slider.interactable) Apply(slider.interactable);
        }

        private void Apply(bool interactable)
        {
            lastState = interactable;
            Color32 fillColor = interactable ? Styles.Primary : Styles.SecondaryText;
            Color32 handleColor = interactable ? Styles.Primary : Styles.SecondaryText;
            if (fillImage != null) fillImage.color = fillColor;
            if (handleImage != null) handleImage.color = handleColor;
            if (canvasGroup != null) canvasGroup.alpha = interactable ? 1f : 0.5f;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            var s = GetComponent<Slider>();
            if (s == null) return;
            if (fillImage == null && s.fillRect != null) fillImage = s.fillRect.GetComponent<Image>();
            if (handleImage == null && s.handleRect != null) handleImage = s.handleRect.GetComponent<Image>();
        }
#endif
    }
}
