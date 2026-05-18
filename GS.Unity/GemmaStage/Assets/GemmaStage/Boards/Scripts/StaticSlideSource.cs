using System;
using System.Collections.Generic;
using UnityEngine;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Phase 4 dev concrete: slide list authored as a serialized <see cref="Texture2D"/> array
    /// in the inspector. Phase 5.2 will add a sibling <c>RuntimeSlideSource</c> populated
    /// from the Lobby file picker; the board doesn't need to know which it has.
    /// </summary>
    [CreateAssetMenu(menuName = "GemmaStage/Boards/Static Slide Source", fileName = "StaticSlideSource")]
    public sealed class StaticSlideSource : SlideSource
    {
        [SerializeField] Texture2D[] slides;

        public override IReadOnlyList<Texture2D> Slides => slides ?? Array.Empty<Texture2D>();

        void OnEnable()
        {
            // Clamp in case the inspector array shrank since CurrentIndex was last persisted.
            if (slides != null && slides.Length > 0)
                CurrentIndex = Mathf.Clamp(CurrentIndex, 0, slides.Length - 1);
            else
                CurrentIndex = 0;
        }
    }
}
