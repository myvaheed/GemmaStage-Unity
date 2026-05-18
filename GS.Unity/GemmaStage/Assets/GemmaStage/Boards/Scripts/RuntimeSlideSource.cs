using System;
using System.Collections.Generic;
using UnityEngine;

namespace GemmaStage.Boards
{
    // Runtime concrete slide source populated from the Lobby file picker. Created
    // via ScriptableObject.CreateInstance from SessionManager (not authored as an
    // asset). The textures it references were rasterized inside
    // PresentationPickerPopup; ownership transfers to SessionManager once Start
    // Session pulls them off the LobbySessionConfig.
    public sealed class RuntimeSlideSource : SlideSource
    {
        Texture2D[] _slides;

        public override IReadOnlyList<Texture2D> Slides => _slides ?? Array.Empty<Texture2D>();

        public void SetSlides(IReadOnlyList<Texture2D> slides)
        {
            if (slides == null || slides.Count == 0)
            {
                _slides = Array.Empty<Texture2D>();
            }
            else
            {
                _slides = new Texture2D[slides.Count];
                for (var i = 0; i < slides.Count; i++)
                    _slides[i] = slides[i];
            }
            CurrentIndex = 0;
            RaiseSlideChanged();
        }
    }
}
