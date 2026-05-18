using System.IO;
using GemmaStage.Core;
using GemmaStage.Session;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemmaStage.Session.Editor
{
    // Run once from the Unity Editor:
    //   GemmaStage menu → Build SessionFailed Panel Prefab
    //
    // Output: Assets/GemmaStage/Session/UI/Prefabs/SessionFailedPanel.prefab
    //
    // Layout (world-space canvas, 640 × 320 logical units, scale 0.00075 — same
    // as the Evaluation panel):
    //   Root — Canvas + TrackedDeviceGraphicRaycaster + CanvasGroup
    //          + LookAtCamera + SessionFailedPanelView
    //     ├─ Background          full-fill RoundedRect24 image, PanelMuted color
    //     └─ Content             VerticalLayoutGroup (header → body → button)
    //          ├─ HeaderText          "Something went wrong" (Error color, bold)
    //          ├─ BodyText            "{role}: {message}" (PrimaryText, word-wrapped)
    //          └─ OkButton            Button_Primary instance, label "OK"
    public static class SessionFailedPanelBuilder
    {
        const string ButtonPrimaryPrefabPath = "Assets/GemmaStage/Core/UI/Prefabs/Button_Primary.prefab";
        const string RoundedRectSpritePath   = "Assets/GemmaStage/Core/UI/Sprites/RoundedRect24.png";
        const string DstPrefabPath           = "Assets/GemmaStage/Session/UI/Prefabs/SessionFailedPanel.prefab";

        const float CanvasWidth  = 640f;
        const float CanvasHeight = 360f;
        const float WorldScale   = 0.00075f;

        const int PaddingOuter   = 28;
        const int Spacing        = 20;
        const int HeaderHeight   = 60;
        const int HeaderFontSize = 30;
        const int BodyFontSize   = 24;
        const int ButtonHeight   = 80;
        const int ButtonFontSize = 26;

        [MenuItem("GemmaStage/Build SessionFailed Panel Prefab")]
        public static void Build()
        {
            var buttonPrimaryPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ButtonPrimaryPrefabPath);
            if (buttonPrimaryPrefab == null)
            {
                Debug.LogError($"[SessionFailedPanelBuilder] Button_Primary prefab not found at {ButtonPrimaryPrefabPath}");
                return;
            }
            var roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(RoundedRectSpritePath);
            if (roundedSprite == null)
            {
                Debug.LogError($"[SessionFailedPanelBuilder] RoundedRect24 sprite not found at {RoundedRectSpritePath}");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DstPrefabPath)!);

            var root = BuildHierarchy(buttonPrimaryPrefab, roundedSprite);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, DstPrefabPath, out bool ok);
                if (ok) Debug.Log($"[SessionFailedPanelBuilder] Saved: {DstPrefabPath}");
                else    Debug.LogError("[SessionFailedPanelBuilder] SaveAsPrefabAsset failed.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        static GameObject BuildHierarchy(GameObject buttonPrimaryPrefab, Sprite roundedSprite)
        {
            var root = new GameObject("SessionFailedPanel");

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

            var view = root.AddComponent<SessionFailedPanelView>();

            // Background
            var bgGo = MakeRectChild(root, "Background");
            StretchFill(bgGo.GetComponent<RectTransform>());
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.sprite        = roundedSprite;
            bgImage.type          = Image.Type.Sliced;
            bgImage.color         = Styles.PanelMuted;
            bgImage.raycastTarget = false;

            // Content
            var content = MakeRectChild(root, "Content");
            StretchFill(content.GetComponent<RectTransform>());

            var vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.padding                = new RectOffset(PaddingOuter, PaddingOuter, PaddingOuter, PaddingOuter);
            vlg.spacing                = Spacing;
            vlg.childAlignment         = TextAnchor.MiddleCenter;
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth      = true;
            vlg.childControlHeight     = true;

            // Header — "Something went wrong" in the Error color so it reads as
            // a problem at a glance.
            var headerGo   = MakeRectChild(content, "HeaderText");
            var headerText = headerGo.AddComponent<TextMeshProUGUI>();
            headerText.text      = "Something went wrong";
            headerText.fontSize  = HeaderFontSize;
            headerText.fontStyle = FontStyles.Bold;
            headerText.color     = Styles.Error;
            headerText.alignment = TextAlignmentOptions.Center;
            AddLayoutElement(headerGo, preferredHeight: HeaderHeight);

            // Body — role + message filled at runtime by SessionFailedPanelView.
            // Flexible height so a long message wraps without pushing the button
            // off-canvas; ellipsis if it really overflows the body slot.
            var bodyGo   = MakeRectChild(content, "BodyText");
            var bodyText = bodyGo.AddComponent<TextMeshProUGUI>();
            bodyText.text                = string.Empty;
            bodyText.fontSize            = BodyFontSize;
            bodyText.color               = Styles.PrimaryText;
            bodyText.alignment           = TextAlignmentOptions.Center;
            bodyText.enableWordWrapping  = true;
            bodyText.overflowMode        = TextOverflowModes.Ellipsis;
            var bodyLe = bodyGo.AddComponent<LayoutElement>();
            bodyLe.flexibleHeight = 1;
            bodyLe.flexibleWidth  = 1;

            // OK button
            var okInst = (GameObject)PrefabUtility.InstantiatePrefab(buttonPrimaryPrefab, content.transform);
            okInst.name = "OkButton";
            EnsureFlexibleWidth(okInst, ButtonHeight);

            var labelText = okInst.GetComponentInChildren<TMP_Text>();
            if (labelText != null)
            {
                labelText.text             = "OK";
                labelText.fontSize         = ButtonFontSize;
                labelText.enableAutoSizing = false;
            }

            var okBtn = okInst.GetComponent<Button>();

            // Wire view fields
            var viewSo = new SerializedObject(view);
            viewSo.FindProperty("bodyText").objectReferenceValue    = bodyText;
            viewSo.FindProperty("okButton").objectReferenceValue    = okBtn;
            viewSo.FindProperty("canvasGroup").objectReferenceValue = cg;
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

        static void AddLayoutElement(GameObject go, float preferredHeight)
        {
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = preferredHeight;
            le.flexibleWidth   = 1;
        }

        static void EnsureFlexibleWidth(GameObject go, float preferredHeight)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.flexibleWidth   = 1;
            le.preferredHeight = preferredHeight;
        }
    }
}
