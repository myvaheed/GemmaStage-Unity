using System;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.IO;
#endif
using UnityEngine;

namespace GemmaStage.Boards
{
    /// <summary>
    /// Captures the current visual state of a board to PNG bytes and raises
    /// <see cref="OnCaptureRequested"/>. Phase 6's <c>GemmaStageService</c>
    /// subscribes to forward the bytes to <c>GS_ConversationSendImage</c>.
    /// </summary>
    /// <remarks>
    /// Editor / development-build only: a <c>dumpToFile</c> toggle writes the PNG to
    /// <c>&lt;projectRoot&gt;/_capture_dumps/</c> for visual validation while no real
    /// subscriber is wired. Stripped from release builds.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class BoardCapture : MonoBehaviour
    {
        public enum BoardKind { Drawing, Presentation }

        [SerializeField] BoardKind kind;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [Tooltip("Editor / dev-build aid: write the captured PNG to <projectRoot>/_capture_dumps for visual inspection.")]
        [SerializeField] bool dumpToFile;
#endif

        public BoardKind Kind => kind;

        /// <summary>
        /// Fired after each <see cref="RequestCapture"/>. Bytes may be null
        /// when the source isn't ready (e.g. board <c>Awake</c> hasn't run yet
        /// or no slide is loaded). Phase 6 subscribers should null-check.
        /// </summary>
        public event Action<BoardKind, byte[]> OnCaptureRequested;

        public void RequestCapture()
        {
            byte[] png = CaptureToPng();
            OnCaptureRequested?.Invoke(kind, png);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (dumpToFile && png != null) DumpToFile(png);
#endif
        }

        public byte[] CaptureToPng()
        {
            switch (kind)
            {
                case BoardKind.Drawing:
                    return CaptureDrawingBoard();
                case BoardKind.Presentation:
                    return CapturePresentationBoard();
                default:
                    return null;
            }
        }

        byte[] CaptureDrawingBoard()
        {
            var db = GetComponent<DrawingBoard>();
            if (db == null || db.Texture == null)
            {
                Debug.LogWarning("[BoardCapture] Drawing: no source texture available");
                return null;
            }
            // The drawing texture is a CPU-side Texture2D managed by DrawingBoard,
            // so the pixels are already on the CPU and EncodeToPNG works directly.
            return db.Texture.EncodeToPNG();
        }

        byte[] CapturePresentationBoard()
        {
            var pb = GetComponent<PresentationBoard>();
            var src = pb != null && pb.Source != null ? pb.Source.CurrentTexture : null;
            if (src == null)
            {
                Debug.LogWarning("[BoardCapture] Presentation: no source texture available");
                return null;
            }

            // Slide source texture is GPU-resident (Texture or RenderTexture). Blit
            // into a temporary RT, read back to a CPU Texture2D, encode PNG.
            int w = src.width;
            int h = src.height;
            var temp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            Texture2D readback = null;
            try
            {
                Graphics.Blit(src, temp);
                RenderTexture.active = temp;
                readback = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                readback.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                readback.Apply(false, false);
                return readback.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(temp);
                if (readback != null)
                {
                    if (Application.isPlaying) Destroy(readback);
                    else DestroyImmediate(readback);
                }
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void DumpToFile(byte[] png)
        {
            var dir = Path.Combine(Application.dataPath, "..", "_capture_dumps");
            Directory.CreateDirectory(dir);
            var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = Path.Combine(dir, $"{kind}_{stamp}.png");
            File.WriteAllBytes(path, png);
            Debug.Log($"[BoardCapture] {kind}: dumped {png.Length}B → {path}");
        }
#endif
    }
}
