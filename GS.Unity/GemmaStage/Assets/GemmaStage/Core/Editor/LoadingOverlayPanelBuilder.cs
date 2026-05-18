using System.IO;
using GemmaStage.Core;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Core.Editor
{
    // Run once from the Unity Editor:
    //   GemmaStage menu → Build Loading Overlay Panel Prefab
    //
    // Output: Assets/GemmaStage/Core/UI/Prefabs/LoadingOverlayPanel.prefab
    //
    // Layout (world-space canvas, deliberately huge so it fills VR FOV when the
    // controller parks it ~0.4 m from the camera):
    //   Root — Canvas + CanvasGroup + LoadingOverlayPanelView
    //     ├─ Background    full-fill solid black Image (alpha 1.0)
    //     └─ Center
    //          ├─ Spinner    rotated each frame by LoadingOverlayPanelView
    //          └─ Label      "Loading…"
    //
    // Background uses the simplest possible solid Image (no sprite) so there is
    // zero chance of a transparent corner from a sliced sprite leaking the env
    // through. Cancel button is intentionally absent (per GS-222 design — the
    // user can only cancel after the env has fully loaded, via End Session).
    public static class LoadingOverlayPanelBuilder
    {
        const string RoundedRectSpritePath = "Assets/GemmaStage/Core/UI/Sprites/RoundedRect24.png";
        const string DstPrefabPath         = "Assets/GemmaStage/Core/UI/Prefabs/LoadingOverlayPanel.prefab";

        // Big enough that at OverlayDistanceMeters=0.4m and WorldScale=0.00075
        // the canvas spans ~1.8 × 1.35 m — comfortably wider than any consumer
        // headset's binocular FOV at that distance.
        const float CanvasWidth  = 2400f;
        const float CanvasHeight = 1800f;
        const float WorldScale   = 0.00075f;

        const int SpinnerSize     = 120; // logical pixels (square)
        const int LabelHeight     = 80;
        const int LabelFontSize   = 40;
        const int CenterSpacing   = 28;

        [MenuItem("GemmaStage/Build Loading Overlay Panel Prefab")]
        public static void Build()
        {
            var roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(RoundedRectSpritePath);
            if (roundedSprite == null)
            {
                Debug.LogError($"[LoadingOverlayPanelBuilder] RoundedRect24 sprite not found at {RoundedRectSpritePath}");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DstPrefabPath)!);

            var root = BuildHierarchy(roundedSprite);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, DstPrefabPath, out bool ok);
                if (ok) Debug.Log($"[LoadingOverlayPanelBuilder] Saved: {DstPrefabPath}");
                else    Debug.LogError("[LoadingOverlayPanelBuilder] SaveAsPrefabAsset failed.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static GameObject BuildHierarchy(Sprite roundedSprite)
        {
            // ── Root ──────────────────────────────────────────────────────────
            var root = new GameObject("LoadingOverlayPanel");

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            // Render last among world-space canvases so it draws on top.
            canvas.sortingOrder = 32000;

            var rt = root.GetComponent<RectTransform>();
            rt.sizeDelta  = new Vector2(CanvasWidth, CanvasHeight);
            rt.localScale = Vector3.one * WorldScale;

            var cg = root.AddComponent<CanvasGroup>();
            // No raycast / no interaction — purely visual occluder.
            cg.blocksRaycasts = false;
            cg.interactable   = false;

            var view = root.AddComponent<LoadingOverlayPanelView>();

            // ── Background (fully opaque black) ───────────────────────────────
            var bgGo = MakeRectChild(root, "Background");
            StretchFill(bgGo.GetComponent<RectTransform>());
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color         = Color.black;
            bgImage.raycastTarget = false;

            // ── Center container (Spinner + Label, vertically stacked) ────────
            var center = MakeRectChild(root, "Center");
            var centerRt = center.GetComponent<RectTransform>();
            centerRt.anchorMin = new Vector2(0.5f, 0.5f);
            centerRt.anchorMax = new Vector2(0.5f, 0.5f);
            centerRt.pivot     = new Vector2(0.5f, 0.5f);
            centerRt.sizeDelta = new Vector2(600f, SpinnerSize + LabelHeight + CenterSpacing);

            var vlg = center.AddComponent<VerticalLayoutGroup>();
            vlg.spacing                = CenterSpacing;
            vlg.childAlignment         = TextAnchor.MiddleCenter;
            vlg.childForceExpandWidth  = false;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth      = false;
            vlg.childControlHeight     = false;

            // ── Spinner — rotating rounded square in the Primary tint ────────
            // We don't have a circular spinner sprite shipped in the project; the
            // rounded-rect square + rotation reads as "loading" without needing
            // a new asset. Replace with a real circular asset later if desired.
            var spinnerGo = MakeRectChild(center, "Spinner");
            var spinnerRt = spinnerGo.GetComponent<RectTransform>();
            spinnerRt.sizeDelta = new Vector2(SpinnerSize, SpinnerSize);
            var spinnerImg = spinnerGo.AddComponent<Image>();
            spinnerImg.sprite        = roundedSprite;
            spinnerImg.type          = Image.Type.Sliced;
            spinnerImg.color         = Styles.Primary;
            spinnerImg.raycastTarget = false;

            // ── Label ─────────────────────────────────────────────────────────
            var labelGo   = MakeRectChild(center, "Label");
            var labelRt   = labelGo.GetComponent<RectTransform>();
            labelRt.sizeDelta = new Vector2(600f, LabelHeight);
            var labelText = labelGo.AddComponent<TextMeshProUGUI>();
            labelText.text      = "Loading…";
            labelText.fontSize  = LabelFontSize;
            labelText.fontStyle = FontStyles.Bold;
            labelText.color     = Styles.PrimaryText;
            labelText.alignment = TextAlignmentOptions.Center;

            // ── Wire view fields ─────────────────────────────────────────────
            var viewSo = new SerializedObject(view);
            viewSo.FindProperty("canvasGroup").objectReferenceValue = cg;
            viewSo.FindProperty("spinner").objectReferenceValue     = spinnerRt;
            viewSo.ApplyModifiedPropertiesWithoutUndo();

            return root;
        }

        static GameObject MakeRectChild(GameObject parent, string childName)
        {
            var go = new GameObject(childName, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        static void StretchFill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
