using UnityEngine;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Displays the current slide from a <see cref="SlideSource"/> on a textured quad.
    /// Subscribes to <see cref="SlideSource.OnSlideChanged"/> and re-binds the texture via
    /// a MaterialPropertyBlock so the shared material asset stays untouched. Public
    /// <see cref="Next"/> / <see cref="Prev"/> passthroughs are what the on-board buttons
    /// (Task 4.2.2) call.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PresentationBoard : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] SlideSource source;

        [Header("Surface")]
        [SerializeField] MeshRenderer slideRenderer;
        [SerializeField] Color emptyFallback = new Color(0.5f, 0.5f, 0.5f);

        MaterialPropertyBlock mpb;
        SlideSource subscribedSource;
        Vector3 slideRendererBaseScale;
        bool baseScaleCaptured;

        static readonly int BaseMapId   = Shader.PropertyToID("_BaseMap");
        static readonly int MainTexId   = Shader.PropertyToID("_MainTex");
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId     = Shader.PropertyToID("_Color");

        public SlideSource Source => source;

        void Awake()
        {
            mpb = new MaterialPropertyBlock();
            CaptureBaseScale();
        }

        void CaptureBaseScale()
        {
            if (baseScaleCaptured || slideRenderer == null) return;
            slideRendererBaseScale = slideRenderer.transform.localScale;
            baseScaleCaptured = true;
        }

        void OnEnable()
        {
            Subscribe(source);
            RefreshSlide();
        }

        void OnDisable() => Unsubscribe(subscribedSource);

        public void Next() => source?.Next();
        public void Prev() => source?.Prev();

        /// <summary>
        /// Swap the slide source at runtime. Phase 5.2 calls this on Lobby → Performance
        /// transition with a <c>RuntimeSlideSource</c> populated from the file picker.
        /// </summary>
        public void SetSource(SlideSource newSource)
        {
            if (newSource == source && subscribedSource == newSource) return;
            Unsubscribe(subscribedSource);
            source = newSource;
            Subscribe(source);
            RefreshSlide();
        }

        void Subscribe(SlideSource s)
        {
            if (s == null) return;
            s.OnSlideChanged += RefreshSlide;
            subscribedSource = s;
        }

        void Unsubscribe(SlideSource s)
        {
            if (s == null) return;
            s.OnSlideChanged -= RefreshSlide;
            if (subscribedSource == s) subscribedSource = null;
        }

        void RefreshSlide()
        {
            if (slideRenderer == null) return;
            CaptureBaseScale();
            if (mpb == null) mpb = new MaterialPropertyBlock();
            slideRenderer.GetPropertyBlock(mpb);

            var tex = source != null ? source.CurrentTexture : null;
            if (tex != null)
            {
                mpb.SetTexture(BaseMapId, tex);
                mpb.SetTexture(MainTexId, tex);
                mpb.SetColor(BaseColorId, Color.white);
                mpb.SetColor(ColorId, Color.white);
                FitToAspect(tex);
            }
            else
            {
                // No slides: bind a neutral white texture and tint via _BaseColor so the
                // quad reads as a flat gray instead of pink-on-missing or environment-lit.
                mpb.SetTexture(BaseMapId, Texture2D.whiteTexture);
                mpb.SetTexture(MainTexId, Texture2D.whiteTexture);
                mpb.SetColor(BaseColorId, emptyFallback);
                mpb.SetColor(ColorId, emptyFallback);
                ResetScale();
            }

            slideRenderer.SetPropertyBlock(mpb);
        }

        // Fit the slide quad to the texture's native aspect ratio so the image renders
        // without stretching or cropping. The unused area around the slide reads as the
        // dark space behind the frame. Accounts for non-uniform parent lossyScale so the
        // *visible* aspect matches the texture, not just the localScale ratio.
        void FitToAspect(Texture tex)
        {
            if (tex.width <= 0 || tex.height <= 0) return;
            float texAspect = (float)tex.width / tex.height;

            // Always start from the authored base scale — never compound previous fits.
            slideRenderer.transform.localScale = slideRendererBaseScale;

            // Effective parent scale = world scale of the renderer / its own localScale.
            // This isolates whatever non-uniform stretch the prefab's ancestors apply.
            Vector3 lossy = slideRenderer.transform.lossyScale;
            float parentSx = Mathf.Abs(slideRendererBaseScale.x) > 1e-6f
                ? lossy.x / slideRendererBaseScale.x : 1f;
            float parentSy = Mathf.Abs(slideRendererBaseScale.y) > 1e-6f
                ? lossy.y / slideRendererBaseScale.y : 1f;
            if (parentSx <= 0f || parentSy <= 0f) { parentSx = parentSy = 1f; }

            // Visible width/height of the unit-quad mesh when localScale == baseScale.
            float visW = slideRendererBaseScale.x * parentSx;
            float visH = slideRendererBaseScale.y * parentSy;
            if (visW <= 0f || visH <= 0f) return;
            float frameAspect = visW / visH;

            Vector3 s = slideRendererBaseScale;
            if (texAspect > frameAspect)
            {
                // Texture wider than frame → fill width, shrink height.
                float targetVisH = visW / texAspect;
                s.y = targetVisH / parentSy;
            }
            else if (texAspect < frameAspect)
            {
                // Texture taller than frame → fill height, shrink width.
                float targetVisW = visH * texAspect;
                s.x = targetVisW / parentSx;
            }
            // else: aspects already match — keep baseScale as-is.

            slideRenderer.transform.localScale = s;
        }

        void ResetScale()
        {
            if (!baseScaleCaptured) return;
            slideRenderer.transform.localScale = slideRendererBaseScale;
        }
    }
}
