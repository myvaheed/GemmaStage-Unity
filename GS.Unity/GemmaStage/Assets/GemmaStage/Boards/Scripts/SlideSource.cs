using System;
using System.Collections.Generic;
using UnityEngine;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Abstract slide list for the Presentation Board. Concrete sources author the slide
    /// array from different inputs (serialized field for dev, file picker at runtime, etc.).
    /// The board subscribes to <see cref="OnSlideChanged"/> and re-binds the current texture
    /// without caring which subclass it has.
    /// </summary>
    public abstract class SlideSource : ScriptableObject
    {
        public abstract IReadOnlyList<Texture2D> Slides { get; }

        public int CurrentIndex { get; protected set; }

        public Texture2D CurrentTexture
        {
            get
            {
                var slides = Slides;
                if (slides == null || slides.Count == 0) return null;
                return slides[Mathf.Clamp(CurrentIndex, 0, slides.Count - 1)];
            }
        }

        public event Action OnSlideChanged;

        public void Next()
        {
            var slides = Slides;
            if (slides == null) return;
            if (CurrentIndex < slides.Count - 1)
            {
                CurrentIndex++;
                OnSlideChanged?.Invoke();
            }
        }

        public void Prev()
        {
            if (CurrentIndex > 0)
            {
                CurrentIndex--;
                OnSlideChanged?.Invoke();
            }
        }

        /// <summary>
        /// Subclasses raise this after they replace the underlying slide list (e.g.
        /// <c>RuntimeSlideSource</c> in Phase 5.2 after the file picker populates it).
        /// </summary>
        protected void RaiseSlideChanged() => OnSlideChanged?.Invoke();
    }
}
