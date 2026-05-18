using System.IO;
using GemmaStage.Core;
using GemmaStage.Evaluation;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemmaStage.Evaluation.Editor
{
    // Run once after opening the project in the Unity Editor:
    //   GemmaStage menu → Build Evaluation Panel Prefab
    //
    // The resulting prefab is saved to
    //   Assets/GemmaStage/Evaluation/UI/Prefabs/EvaluationPanel.prefab
    //
    // Layout (world-space canvas, 600 × 320 logical units, scale 0.00075):
    //   Root — Canvas + TrackedDeviceGraphicRaycaster + CanvasGroup
    //          + LookAtCamera (yawOnly=false) + EvaluationPanelView
    //     ├─ Background          full-fill Image, PanelMuted color (matches Live/Final/EndSession)
    //     └─ Content             VerticalLayoutGroup with padding/spacing
    //          ├─ HeaderText           "Analyzing your presentation…" (heading typography)
    //          ├─ ProgressBarContainer
    //          │    ├─ ProgressBarBackground   RoundedRect24, Divider color
    //          │    └─ ProgressBarFill         RoundedRect24, Primary color, anchored stretch
    //          ├─ PercentageLabel       "0%" (heading typography)
    //          └─ CancelButton          Button_Secondary instance
    public static class EvaluationPanelBuilder
    {
        const string ButtonSecondaryPrefabPath = "Assets/GemmaStage/Core/UI/Prefabs/Button_Secondary.prefab";
        const string RoundedRectSpritePath     = "Assets/GemmaStage/Core/UI/Sprites/RoundedRect24.png";
        const string DstPrefabPath             = "Assets/GemmaStage/Evaluation/UI/Prefabs/EvaluationPanel.prefab";

        // Canvas logical size and world scale — matches FinalQaPanel / LiveQaPanel
        // for the world scale. Height is taller than other panels because the
        // evaluation panel stacks four content rows (header, bar, %, button).
        const float CanvasWidth  = 640f;
        const float CanvasHeight = 420f;
        const float WorldScale   = 0.00075f;

        // Layout constants (logical pixels).
        const int PaddingOuter      = 28;
        const int SpacingHeader     = 32;  // header → loader (extra breathing room)
        const int SpacingDefault    = 20;  // loader → % → button
        const int HeaderHeight      = 88;  // fits two lines of 30 pt bold
        const int HeaderFontSize    = 30;
        const int ProgressHeight    = 32;
        const int PercentHeight     = 40;
        const int PercentFontSize   = 30;
        const int ButtonHeight      = 80;
        const int ButtonFontSize    = 26;

        [MenuItem("GemmaStage/Build Evaluation Panel Prefab")]
        public static void Build()
        {
            var buttonSecondaryPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ButtonSecondaryPrefabPath);
            if (buttonSecondaryPrefab == null)
            {
                Debug.LogError($"[EvaluationPanelBuilder] Button_Secondary prefab not found at {ButtonSecondaryPrefabPath}");
                return;
            }
            var roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(RoundedRectSpritePath);
            if (roundedSprite == null)
            {
                Debug.LogError($"[EvaluationPanelBuilder] RoundedRect24 sprite not found at {RoundedRectSpritePath}");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DstPrefabPath)!);

            // Build hierarchy in the current scene as a temporary object.
            var root = BuildHierarchy(buttonSecondaryPrefab, roundedSprite);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, DstPrefabPath, out bool ok);
                if (ok) Debug.Log($"[EvaluationPanelBuilder] Saved: {DstPrefabPath}");
                else    Debug.LogError("[EvaluationPanelBuilder] SaveAsPrefabAsset failed.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static GameObject BuildHierarchy(GameObject buttonSecondaryPrefab, Sprite roundedSprite)
        {
            // ── Root ──────────────────────────────────────────────────────────
            var root = new GameObject("EvaluationPanel");

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rt = root.GetComponent<RectTransform>();
            rt.sizeDelta  = new Vector2(CanvasWidth, CanvasHeight);
            rt.localScale = Vector3.one * WorldScale;

            root.AddComponent<TrackedDeviceGraphicRaycaster>();
            var cg = root.AddComponent<CanvasGroup>();

            var lac   = root.AddComponent<LookAtCamera>();
            var lacSo = new SerializedObject(lac);
            lacSo.FindProperty("yawOnly").boolValue = false;
            lacSo.ApplyModifiedPropertiesWithoutUndo();

            var view = root.AddComponent<EvaluationPanelView>();

            // ── Background (rounded — Styles.Layout.PanelCornerRadius via RoundedRect24) ──
            var bgGo = MakeRectChild(root, "Background");
            StretchFill(bgGo.GetComponent<RectTransform>());
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.sprite        = roundedSprite;
            bgImage.type          = Image.Type.Sliced;
            bgImage.color         = Styles.PanelMuted;
            bgImage.raycastTarget = false;

            // ── Content (VerticalLayoutGroup over Background) ─────────────────
            var content = MakeRectChild(root, "Content");
            StretchFill(content.GetComponent<RectTransform>());

            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.padding                = new RectOffset(PaddingOuter, PaddingOuter, PaddingOuter, PaddingOuter);
            vlg.spacing                = SpacingDefault;
            vlg.childAlignment         = TextAnchor.MiddleCenter;
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth      = true;
            vlg.childControlHeight     = true;

            // ── HeaderText ────────────────────────────────────────────────────
            // Extra bottom margin pushes the loader away from the header so the
            // wrapped two-line "Analyzing your presentation…" doesn't crowd the bar.
            var headerGo   = MakeRectChild(content, "HeaderText");
            var headerText = headerGo.AddComponent<TextMeshProUGUI>();
            headerText.text      = "Analyzing your presentation…";
            headerText.fontSize  = HeaderFontSize;
            headerText.fontStyle = FontStyles.Bold;
            headerText.color     = Styles.PrimaryText;
            headerText.alignment = TextAlignmentOptions.Center;
            headerText.margin    = new Vector4(0, 0, 0, SpacingHeader - SpacingDefault);
            AddLayoutElement(headerGo, preferredHeight: HeaderHeight);

            // ── ProgressBarContainer ──────────────────────────────────────────
            var barContainer = MakeRectChild(content, "ProgressBarContainer");
            AddLayoutElement(barContainer, preferredHeight: ProgressHeight);

            // Background — rounded slot (Divider color, sliced).
            var barBgGo = MakeRectChild(barContainer, "ProgressBarBackground");
            StretchFill(barBgGo.GetComponent<RectTransform>());
            var barBgImg = barBgGo.AddComponent<Image>();
            barBgImg.sprite        = roundedSprite;
            barBgImg.type          = Image.Type.Sliced;
            barBgImg.color         = Styles.Divider;
            barBgImg.raycastTarget = false;

            // Fill — rounded slot anchor-stretched, anchorMax.x driven by SetProgress.
            // Both ends of the fill stay rounded as it grows because slicing is preserved.
            var fillGo = MakeRectChild(barContainer, "ProgressBarFill");
            var fillRt = fillGo.GetComponent<RectTransform>();
            fillRt.anchorMin = new Vector2(0f, 0f);
            fillRt.anchorMax = new Vector2(0f, 1f);   // start at 0 % — runtime drives anchorMax.x
            fillRt.offsetMin = Vector2.zero;
            fillRt.offsetMax = Vector2.zero;
            var fillImg = fillGo.AddComponent<Image>();
            fillImg.sprite        = roundedSprite;
            fillImg.type          = Image.Type.Sliced;
            fillImg.color         = Styles.Primary;
            fillImg.raycastTarget = false;

            // ── PercentageLabel ───────────────────────────────────────────────
            var pctGo   = MakeRectChild(content, "PercentageLabel");
            var pctText = pctGo.AddComponent<TextMeshProUGUI>();
            pctText.text      = "0%";
            pctText.fontSize  = PercentFontSize;
            pctText.fontStyle = FontStyles.Bold;
            pctText.color     = Styles.Primary;
            pctText.alignment = TextAlignmentOptions.Center;
            AddLayoutElement(pctGo, preferredHeight: PercentHeight);

            // ── CancelButton (Button_Secondary instance) ──────────────────────
            var cancelInst = (GameObject)PrefabUtility.InstantiatePrefab(buttonSecondaryPrefab, content.transform);
            cancelInst.name = "CancelButton";
            // Override prefab's LayoutElement so the button stretches across the
            // panel width and is tall enough to fit the long label on one line.
            EnsureFlexibleWidth(cancelInst, ButtonHeight);

            // Set button label text + smaller font so the long string fits.
            var labelText = cancelInst.GetComponentInChildren<TMP_Text>();
            if (labelText != null)
            {
                labelText.text             = "Cancel Analyzing / Return to Lobby";
                labelText.fontSize         = ButtonFontSize;
                labelText.enableAutoSizing = false;
                labelText.enableWordWrapping = false;
                labelText.overflowMode     = TextOverflowModes.Ellipsis;
            }

            var cancelBtn = cancelInst.GetComponent<Button>();

            // ── Wire EvaluationPanelView fields ───────────────────────────────
            var viewSo = new SerializedObject(view);
            viewSo.FindProperty("headerLabel").objectReferenceValue     = headerText;
            viewSo.FindProperty("progressBarFill").objectReferenceValue = fillImg;
            viewSo.FindProperty("percentageLabel").objectReferenceValue = pctText;
            viewSo.FindProperty("cancelButton").objectReferenceValue    = cancelBtn;
            viewSo.FindProperty("canvasGroup").objectReferenceValue     = cg;
            viewSo.ApplyModifiedPropertiesWithoutUndo();

            return root;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

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

        static void AddLayoutElement(GameObject go, float preferredHeight)
        {
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = preferredHeight;
            le.flexibleWidth   = 1;
        }

        // Ensures a prefab-instantiated row stretches to the panel width even when its
        // own LayoutElement has flexibleWidth=-1 (default for Button_Secondary).
        static void EnsureFlexibleWidth(GameObject go, float preferredHeight)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.flexibleWidth   = 1;
            le.preferredHeight = preferredHeight;
        }
    }
}
