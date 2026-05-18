using System;
using System.Collections.Generic;
using UnityEngine;

namespace GemmaStage.Boards
{
    /// <summary>
    /// VR whiteboard backed by a CPU-side <see cref="Texture2D"/>. Markers call
    /// <see cref="BeginStroke"/> / <see cref="ExtendStroke"/> / <see cref="EndStroke"/>
    /// while their tip is within proximity of the surface plane; pixels are stamped
    /// as filled circles into a Color32 buffer and pushed to the GPU once per frame
    /// in <see cref="LateUpdate"/>.
    ///
    /// Why CPU pixel paint instead of a RenderTexture + draw camera: in URP the per-camera
    /// clear is non-optional when the camera is bound to a RenderTexture, and submission
    /// timing is asynchronous enough that toggling a baker MeshRenderer around
    /// <c>drawCamera.Render()</c> reliably drops the bake. The Texture2D path used by
    /// Valve's "Painting" reference sample (see <c>EXAMPLES/Painting</c>) sidesteps both
    /// problems and matches our needs (one whiteboard surface, no shader effects).
    /// </summary>
    [DisallowMultipleComponent]
    public class DrawingBoard : MonoBehaviour
    {
        [Header("Surface")]
        [SerializeField] Transform surfaceTransform;
        [SerializeField] Renderer surfaceRenderer;
        [Tooltip("Drawable surface size in surfaceTransform local space. UV (0,0)..(1,1) maps to a rectangle of this size centred on the transform's origin in its XY plane.")]
        [SerializeField] Vector2 surfaceLocalSize = new Vector2(2f, 1.5f);

        [Header("Texture")]
        [SerializeField] Vector2Int textureResolution = new Vector2Int(1024, 768);
        [SerializeField] Color background = Color.white;

        [Header("Brush")]
        [Tooltip("Brush radius in pixels. ~6 px on a 1024×768 board ≈ 1.2 cm marker stroke on a 2 m × 1.5 m surface.")]
        [SerializeField] int brushRadiusPixels = 6;

        [Tooltip("Maximum number of strokes that can be undone. One snapshot of the pixel buffer is kept per stroke, so memory cost is roughly width × height × 4 bytes × this count.")]
        [SerializeField] int undoCapacity = 32;

        [Header("Debug")]
        [Tooltip("Log stroke begin/end + texture upload events to the console. Temporary diagnostic; turn off when drawing is verified working.")]
        [SerializeField] bool debugLog;

        Texture2D texture;
        Color32[] pixels;
        Color32 backgroundPx;
        bool dirty;

        bool strokeActive;
        Vector2 lastUV;
        Color32 currentColorPx;

        // Snapshot of the pixel buffer captured at BeginStroke. Undo restores the most
        // recent snapshot. We use a list as a bounded ring buffer so memory is capped.
        readonly List<Color32[]> undoSnapshots = new();

        Material runtimeSurfaceMaterial;

        static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        public Texture2D Texture => texture;
        public Vector2 SurfaceLocalSize => surfaceLocalSize;
        public Transform SurfaceTransform => surfaceTransform;

        void Awake()
        {
            if (!ValidateWiring()) { enabled = false; return; }

            texture = new Texture2D(textureResolution.x, textureResolution.y, TextureFormat.RGBA32, false, false)
            {
                name = $"{name}_DrawSurface",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            backgroundPx = background;
            pixels = new Color32[textureResolution.x * textureResolution.y];
            FillBackground();
            texture.SetPixels32(pixels);
            texture.Apply(false, false);

            // MaterialPropertyBlock would be the lightweight choice, but URP/Lit's
            // sampler bindings don't reliably honour MPB texture overrides for _BaseMap
            // (the shader still samples whatever the material's serialized _BaseMap is,
            // which is empty for DrawingSurface.mat — the painted Texture2D never
            // appears on the surface even though the GPU upload is verifiably
            // happening every frame). Assigning through .material instantiates a
            // per-renderer material clone which DOES pick up the texture, at the cost
            // of a one-time material instance allocation.
            // Build a fresh URP/Unlit material from scratch instead of cloning
            // DrawingSurface.mat. The cloned instance was confirmed to have our
            // texture in its _BaseMap slot but the surface still rendered plain
            // white — pointing at inherited shader state on the source material
            // (likely a zeroed _BaseMap_ST tiling vector). A brand-new material
            // starts with default tiling (1,1,0,0) and no other inherited state.
            var unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (unlitShader == null)
            {
                Debug.LogError("[DrawingBoard] URP/Unlit shader not found in build.", this);
                return;
            }
            runtimeSurfaceMaterial = new Material(unlitShader) { name = $"{name}_DrawSurfaceMat" };
            runtimeSurfaceMaterial.SetTexture(BaseMapId, texture);
            runtimeSurfaceMaterial.SetTexture(MainTexId, texture);
            runtimeSurfaceMaterial.mainTexture = texture;
            runtimeSurfaceMaterial.SetTextureScale(BaseMapId, Vector2.one);
            runtimeSurfaceMaterial.SetTextureOffset(BaseMapId, Vector2.zero);
            // The DrawingBoard prefab is rotated +90° about Y in the scene which
            // flips the built-in Quad mesh's normal away from the player. Default
            // back-face culling then hides the painted surface and the player only
            // sees the BoardBack frame behind it. Render both sides so orientation
            // doesn't matter.
            runtimeSurfaceMaterial.SetFloat("_Cull", 0);
            surfaceRenderer.material = runtimeSurfaceMaterial;

            if (debugLog)
            {
                var bound = runtimeSurfaceMaterial.GetTexture(BaseMapId);
                var st = runtimeSurfaceMaterial.GetVector("_BaseMap_ST");
                Debug.Log(
                    $"[DrawingBoard] surfaceRenderer='{surfaceRenderer.name}' " +
                    $"material='{runtimeSurfaceMaterial.name}' shader='{runtimeSurfaceMaterial.shader.name}' " +
                    $"_BaseMap='{(bound != null ? bound.name : "null")}' " +
                    $"_BaseMap_ST=({st.x},{st.y},{st.z},{st.w}) " +
                    $"texSize={textureResolution.x}x{textureResolution.y} " +
                    $"keywords=[{string.Join(",", runtimeSurfaceMaterial.shaderKeywords)}]",
                    this);

                // Dump the initial texture state to PNG so we can verify out-of-band
                // that the test pattern actually got into the Texture2D. If this PNG
                // shows the colored corners but the board surface doesn't, the bug
                // is on the GPU sampling side.
                try
                {
                    var dir = System.IO.Path.Combine(Application.dataPath, "..", "_capture_dumps");
                    System.IO.Directory.CreateDirectory(dir);
                    var path = System.IO.Path.Combine(dir, "diagnostic_initial.png");
                    System.IO.File.WriteAllBytes(path, texture.EncodeToPNG());
                    Debug.Log($"[DrawingBoard] dumped initial texture state → {path}", this);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[DrawingBoard] PNG dump failed: {ex.Message}", this);
                }
            }
        }

        bool ValidateWiring()
        {
            bool ok = true;
            if (surfaceTransform == null) { Debug.LogError("[DrawingBoard] surfaceTransform unwired.", this); ok = false; }
            if (surfaceRenderer  == null) { Debug.LogError("[DrawingBoard] surfaceRenderer unwired.",  this); ok = false; }
            return ok;
        }

        void OnDestroy()
        {
            if (texture != null)
            {
                if (Application.isPlaying) Destroy(texture);
                else DestroyImmediate(texture);
                texture = null;
            }
            if (runtimeSurfaceMaterial != null)
            {
                if (Application.isPlaying) Destroy(runtimeSurfaceMaterial);
                else DestroyImmediate(runtimeSurfaceMaterial);
                runtimeSurfaceMaterial = null;
            }
            undoSnapshots.Clear();
        }

        void LateUpdate()
        {
            if (!dirty) return;
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            dirty = false;
            if (debugLog) Debug.Log($"[DrawingBoard] flushed {pixels.Length} px → GPU (frame {Time.frameCount})", this);
        }

        public void BeginStroke(Color color, Vector2 uv)
        {
            if (strokeActive) EndStroke();
            PushUndoSnapshot();
            currentColorPx = color;
            lastUV = uv;
            strokeActive = true;
            StampAtUV(uv);
            if (debugLog) Debug.Log($"[DrawingBoard] BeginStroke uv=({uv.x:F3},{uv.y:F3}) color=({color.r:F2},{color.g:F2},{color.b:F2})", this);
        }

        public void ExtendStroke(Vector2 uv)
        {
            if (!strokeActive) return;
            DrawSegment(lastUV, uv);
            lastUV = uv;
        }

        public void EndStroke()
        {
            strokeActive = false;
        }

        public void Undo()
        {
            if (strokeActive) EndStroke();
            int n = undoSnapshots.Count;
            if (n == 0) return;
            var snap = undoSnapshots[n - 1];
            undoSnapshots.RemoveAt(n - 1);
            Array.Copy(snap, pixels, pixels.Length);
            dirty = true;
        }

        void PushUndoSnapshot()
        {
            // Drop the oldest snapshot when we exceed capacity. We never reuse buffers
            // across snapshots — each push allocates a fresh array, since holding
            // references to the live `pixels` array would mean every snapshot reflects
            // the latest state, defeating undo.
            if (undoSnapshots.Count >= undoCapacity)
                undoSnapshots.RemoveAt(0);
            var snap = new Color32[pixels.Length];
            Array.Copy(pixels, snap, pixels.Length);
            undoSnapshots.Add(snap);
        }

        public bool TryWorldToUV(Vector3 worldPos, out Vector2 uv)
        {
            var local = surfaceTransform.InverseTransformPoint(worldPos);
            uv = new Vector2(
                local.x / surfaceLocalSize.x + 0.5f,
                local.y / surfaceLocalSize.y + 0.5f);
            return uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
        }

        /// <summary>
        /// Projects <paramref name="worldPos"/> onto the board's surface plane. Returns true when
        /// the point is within the active band (front side: +Z up to <paramref name="frontDist"/>;
        /// back side: -Z down to -<paramref name="backDist"/>) AND inside the UV bounds.
        /// Front/back is in <c>surfaceTransform</c>'s local Z — +Z is the painted face the player
        /// approaches from. The asymmetric band lets the user press the marker through the
        /// board without losing the active drawing zone, while keeping a tight no-premature-draw
        /// tolerance on the approach side.
        /// </summary>
        public bool TryWorldToUVWithProximity(Vector3 worldPos, float frontDist, float backDist, out Vector2 uv)
        {
            var local = surfaceTransform.InverseTransformPoint(worldPos);
            if (local.z > frontDist || local.z < -backDist) { uv = default; return false; }
            uv = new Vector2(
                local.x / surfaceLocalSize.x + 0.5f,
                local.y / surfaceLocalSize.y + 0.5f);
            return uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
        }

        public Vector3 UVToWorld(Vector2 uv, float surfaceLocalZ = 0f)
        {
            var local = new Vector3(
                (uv.x - 0.5f) * surfaceLocalSize.x,
                (uv.y - 0.5f) * surfaceLocalSize.y,
                surfaceLocalZ);
            return surfaceTransform.TransformPoint(local);
        }

        void FillBackground()
        {
            for (int i = 0; i < pixels.Length; i++) pixels[i] = backgroundPx;
        }


        void StampAtUV(Vector2 uv)
        {
            int cx = Mathf.RoundToInt(uv.x * (textureResolution.x - 1));
            int cy = Mathf.RoundToInt(uv.y * (textureResolution.y - 1));
            StampCircle(cx, cy);
        }

        void StampCircle(int cx, int cy)
        {
            int r = brushRadiusPixels;
            int r2 = r * r;
            int w = textureResolution.x;
            int h = textureResolution.y;
            int xMin = Mathf.Max(0, cx - r);
            int xMax = Mathf.Min(w - 1, cx + r);
            int yMin = Mathf.Max(0, cy - r);
            int yMax = Mathf.Min(h - 1, cy + r);
            for (int y = yMin; y <= yMax; y++)
            {
                int dy = y - cy;
                int dy2 = dy * dy;
                int row = y * w;
                for (int x = xMin; x <= xMax; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dy2 <= r2)
                        pixels[row + x] = currentColorPx;
                }
            }
            dirty = true;
        }

        void DrawSegment(Vector2 fromUV, Vector2 toUV)
        {
            // Bresenham line walk between the two pixel coordinates, stamping a brush
            // circle at every step. Each circle overlaps the previous (stride = 1 px),
            // which is wasteful but cheap at our brush sizes and gives an unbroken line
            // even when the marker traverses many pixels in a single frame.
            int x0 = Mathf.RoundToInt(fromUV.x * (textureResolution.x - 1));
            int y0 = Mathf.RoundToInt(fromUV.y * (textureResolution.y - 1));
            int x1 = Mathf.RoundToInt(toUV.x   * (textureResolution.x - 1));
            int y1 = Mathf.RoundToInt(toUV.y   * (textureResolution.y - 1));

            int dx =  Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                StampCircle(x0, y0);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }
    }
}
