using System.IO;
using GemmaStage.Core;
using GemmaStage.Results;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace GemmaStage.Results.Editor
{
    // Run once after opening the Unity project:
    //   GemmaStage menu → Build Results Panel Prefabs
    //
    // Outputs (world-space canvases at 0.00075 scale):
    //   Assets/GemmaStage/Results/UI/Prefabs/CriterionRow.prefab   (760 u wide, auto-height)
    //   Assets/GemmaStage/Results/UI/Prefabs/ResultsPanel.prefab   (760 × 720 u)
    //
    // ResultsPanel references CriterionRow — the builder wires this automatically.
    //
    // Layout (ResultsPanel) — mirrors the Lobby PresentationPickerPopup scroll
    // pattern (Image-on-ScrollView with raycast=true + Mask-on-Viewport with
    // alpha≈0.01 Image), which is the ray-draggable pattern proven to work
    // with TrackedDeviceGraphicRaycaster on world-space canvases:
    //   Root — Canvas + TrackedDeviceGraphicRaycaster + CanvasGroup
    //          + LookAtCamera(yawOnly=false) + ResultsPanelView
    //     ├─ Background        RoundedRect24, PanelMuted, raycast=false
    //     └─ Layout            VerticalLayoutGroup (padding, spacing)
    //          ├─ Header       TMP title (44 px high, 24 pt)
    //          ├─ ScrollView   fills remaining space; Image (raycast=true)
    //          │    └─ Viewport (Mask + alpha≈0.01 Image raycast=true)
    //          │         └─ Content (VLG + ContentSizeFitter, top-anchored)
    //          └─ Footer       centered HLG — Save as PDF + Go to Lobby (220×48)
    //
    // Layout (CriterionRow):
    //   Root — VerticalLayoutGroup + ContentSizeFitter + ResultsCriterionRow
    //     ├─ Separator         1 px Divider
    //     ├─ RowHeader         HorizontalLayoutGroup (72 px)
    //     │    ├─ ScoreBadge   LayoutElement 52×52
    //     │    │    ├─ BadgeBackground  Image RoundedRect24 (tinted by score)
    //     │    │    └─ ScoreText        TMP centered
    //     │    ├─ NameLabel    TMP flexible
    //     │    └─ DetailsToggle  Button 120 px wide
    //     │         └─ ToggleLabel  TMP "▸ Details"
    //     ├─ VerdictText       TMP body, left-indented
    //     └─ DetailsPanel      TMP (hidden initially)
    public static class ResultsPanelBuilder
    {
        const string ButtonPrimaryPrefabPath   = "Assets/GemmaStage/Core/UI/Prefabs/Button_Primary.prefab";
        const string ButtonSecondaryPrefabPath = "Assets/GemmaStage/Core/UI/Prefabs/Button_Secondary.prefab";
        const string RoundedRectSpritePath     = "Assets/GemmaStage/Core/UI/Sprites/RoundedRect24.png";
        const string RowPrefabPath             = "Assets/GemmaStage/Results/UI/Prefabs/CriterionRow.prefab";
        const string ExpandablePrefabPath      = "Assets/GemmaStage/Results/UI/Prefabs/ExpandableSection.prefab";
        const string PanelPrefabPath           = "Assets/GemmaStage/Results/UI/Prefabs/ResultsPanel.prefab";

        const float CanvasWidth  = 760f;
        const float CanvasHeight = 1000f;
        const float WorldScale   = 0.00075f;

        // Layout constants (logical pixels).
        const int PaddingOuter      = 20;
        const int ContentSpacing    = 12;
        const int HeaderHeight      = 44;
        const int HeaderFontSize    = 24;
        const int FooterHeight      = 48;
        const int FooterButtonWidth = 220;
        const int ButtonFontSize    = 22;
        const int ScrollPadding     = 4;  // inner scroll content padding
        const int DividerHeight     = 1;  // per-row separator inside CriterionRow

        // CriterionRow constants.
        const int BadgeSize         = 52;
        const int RowHeaderHeight   = 72;
        const int NameFontSize      = 28;
        const int ToggleWidth       = 124;
        const int ToggleFontSize    = 22;
        const int VerdictFontSize   = 26;
        const int DetailsFontSize   = 22;

        [MenuItem("GemmaStage/Build Results Panel Prefabs")]
        public static void Build()
        {
            var btnPrimary = AssetDatabase.LoadAssetAtPath<GameObject>(ButtonPrimaryPrefabPath);
            if (btnPrimary == null)
            {
                Debug.LogError($"[ResultsPanelBuilder] Button_Primary prefab not found: {ButtonPrimaryPrefabPath}");
                return;
            }
            var btnSecondary = AssetDatabase.LoadAssetAtPath<GameObject>(ButtonSecondaryPrefabPath);
            if (btnSecondary == null)
            {
                Debug.LogError($"[ResultsPanelBuilder] Button_Secondary prefab not found: {ButtonSecondaryPrefabPath}");
                return;
            }
            var roundedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(RoundedRectSpritePath);
            if (roundedSprite == null)
            {
                Debug.LogError($"[ResultsPanelBuilder] RoundedRect24 sprite not found: {RoundedRectSpritePath}");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(RowPrefabPath)!);

            // ── 1. Build CriterionRow prefab ──────────────────────────────────
            var rowRoot = BuildCriterionRow(roundedSprite);
            ResultsCriterionRow rowComp;
            try
            {
                PrefabUtility.SaveAsPrefabAsset(rowRoot, RowPrefabPath, out bool ok);
                if (ok) Debug.Log($"[ResultsPanelBuilder] Saved: {RowPrefabPath}");
                else    Debug.LogError("[ResultsPanelBuilder] SaveAsPrefabAsset failed for CriterionRow.");
            }
            finally { Object.DestroyImmediate(rowRoot); }

            // Load the saved prefab so we can reference it from ResultsPanel.
            var rowPrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(RowPrefabPath);
            rowComp = rowPrefabAsset != null ? rowPrefabAsset.GetComponent<ResultsCriterionRow>() : null;

            // ── 2. Build ExpandableSection prefab ─────────────────────────────
            var sectionRoot = BuildExpandableSection(roundedSprite);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(sectionRoot, ExpandablePrefabPath, out bool ok);
                if (ok) Debug.Log($"[ResultsPanelBuilder] Saved: {ExpandablePrefabPath}");
                else    Debug.LogError("[ResultsPanelBuilder] SaveAsPrefabAsset failed for ExpandableSection.");
            }
            finally { Object.DestroyImmediate(sectionRoot); }
            var sectionPrefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ExpandablePrefabPath);
            var sectionComp = sectionPrefabAsset != null ? sectionPrefabAsset.GetComponent<ResultsExpandableSection>() : null;

            // ── 3. Build ResultsPanel prefab ──────────────────────────────────
            var panelRoot = BuildPanel(btnPrimary, btnSecondary, roundedSprite, rowComp, sectionComp);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(panelRoot, PanelPrefabPath, out bool ok);
                if (ok) Debug.Log($"[ResultsPanelBuilder] Saved: {PanelPrefabPath}");
                else    Debug.LogError("[ResultsPanelBuilder] SaveAsPrefabAsset failed for ResultsPanel.");
            }
            finally { Object.DestroyImmediate(panelRoot); }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // ── CriterionRow ──────────────────────────────────────────────────────

        static GameObject BuildCriterionRow(Sprite roundedSprite)
        {
            var root = new GameObject("CriterionRow", typeof(RectTransform));

            // Root layout — auto-height driven by its children.
            var vlg = root.AddComponent<VerticalLayoutGroup>();
            vlg.padding               = new RectOffset(0, 0, 0, 8);
            vlg.spacing               = 0;
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth      = true;
            vlg.childControlHeight     = true;

            var csf = root.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var le = root.AddComponent<LayoutElement>();
            le.flexibleWidth = 1;

            // ── Separator (top divider) ───────────────────────────────────────
            var sep = MakeRectChild(root, "Separator");
            var sepImg = sep.AddComponent<Image>();
            sepImg.color         = Styles.Divider;
            sepImg.raycastTarget = false;
            AddLE(sep, preferredHeight: DividerHeight);

            // ── RowHeader ─────────────────────────────────────────────────────
            var header = MakeRectChild(root, "RowHeader");
            AddLE(header, preferredHeight: RowHeaderHeight);

            var hlg = header.AddComponent<HorizontalLayoutGroup>();
            hlg.padding               = new RectOffset(16, 16, 10, 10);
            hlg.spacing               = 12;
            hlg.childAlignment        = TextAnchor.MiddleLeft;
            hlg.childForceExpandWidth  = false;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth      = true;
            hlg.childControlHeight     = true;

            // Score badge container (52×52, fixed)
            var badge = MakeRectChild(header, "ScoreBadge");
            var badgeLe = badge.AddComponent<LayoutElement>();
            badgeLe.preferredWidth  = BadgeSize;
            badgeLe.preferredHeight = BadgeSize;
            badgeLe.flexibleWidth   = 0;
            badgeLe.flexibleHeight  = 0;

            // Badge background image (RoundedRect24 sliced — at 52×52 renders pill-like)
            var badgeBg = MakeRectChild(badge, "BadgeBackground");
            StretchFill(badgeBg);
            var badgeBgImg = badgeBg.AddComponent<Image>();
            badgeBgImg.sprite        = roundedSprite;
            badgeBgImg.type          = Image.Type.Sliced;
            badgeBgImg.color         = Styles.Primary;   // overridden at runtime
            badgeBgImg.raycastTarget = false;

            // Score label centred inside badge
            var scoreLabelGo = MakeRectChild(badge, "ScoreText");
            StretchFill(scoreLabelGo);
            var scoreTmp = scoreLabelGo.AddComponent<TextMeshProUGUI>();
            scoreTmp.text      = "5";
            scoreTmp.fontSize  = 22;
            scoreTmp.fontStyle = FontStyles.Bold;
            scoreTmp.color     = Styles.PrimaryText;
            scoreTmp.alignment = TextAlignmentOptions.Center;
            scoreTmp.raycastTarget = false;

            // Name label (flexible width)
            var nameGo = MakeRectChild(header, "NameLabel");
            var nameLe = nameGo.AddComponent<LayoutElement>();
            nameLe.flexibleWidth = 1;
            var nameTmp = nameGo.AddComponent<TextMeshProUGUI>();
            nameTmp.text      = "Criterion";
            nameTmp.fontSize  = NameFontSize;
            nameTmp.fontStyle = FontStyles.Bold;
            nameTmp.color     = Styles.PrimaryText;
            nameTmp.alignment = TextAlignmentOptions.MidlineLeft;
            nameTmp.enableWordWrapping = false;
            nameTmp.overflowMode  = TextOverflowModes.Ellipsis;
            nameTmp.raycastTarget = false;

            // Details toggle button. Carries its own Image (raycast target) on
            // the GameObject itself so the ray catches the whole toggle area
            // — same trap as the ExpandableSection header: a child Image
            // would be sized to nothing by the parent HLG. Inside, a small
            // HLG arranges [chevron icon] + ["Details" label]; the chevron
            // is the built-in DropdownArrow sprite, rotated -90° when
            // collapsed (matches the section headers).
            var dropdownArrow = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd");
            const int ChevronSize = 16;

            var toggleGo = MakeRectChild(header, "DetailsToggle");
            var toggleLe = toggleGo.AddComponent<LayoutElement>();
            toggleLe.preferredWidth  = ToggleWidth;
            toggleLe.flexibleWidth   = 0;

            var toggleBg = toggleGo.AddComponent<Image>();
            toggleBg.color         = new Color(1f, 1f, 1f, 0.02f);
            toggleBg.raycastTarget = true;

            var toggleBtn = toggleGo.AddComponent<Button>();
            toggleBtn.targetGraphic = toggleBg;
            var toggleColors = toggleBtn.colors;
            toggleColors.normalColor      = new Color(1, 1, 1, 0.02f);
            toggleColors.highlightedColor = new Color(1, 1, 1, 0.10f);
            toggleColors.pressedColor     = new Color(1, 1, 1, 0.18f);
            toggleColors.selectedColor    = toggleColors.normalColor;
            toggleBtn.colors = toggleColors;

            var toggleHlg = toggleGo.AddComponent<HorizontalLayoutGroup>();
            toggleHlg.padding               = new RectOffset(8, 8, 0, 0);
            toggleHlg.spacing               = 6;
            toggleHlg.childAlignment        = TextAnchor.MiddleRight;
            toggleHlg.childForceExpandWidth  = false;
            toggleHlg.childForceExpandHeight = true;
            toggleHlg.childControlWidth      = true;
            toggleHlg.childControlHeight     = true;

            var chevronGo = MakeRectChild(toggleGo, "Chevron");
            var chevronLe = chevronGo.AddComponent<LayoutElement>();
            chevronLe.preferredWidth  = ChevronSize;
            chevronLe.preferredHeight = ChevronSize;
            chevronLe.flexibleWidth   = 0;
            chevronLe.flexibleHeight  = 0;
            var chevronImg = chevronGo.AddComponent<Image>();
            chevronImg.sprite         = dropdownArrow;
            chevronImg.color          = Styles.SecondaryText;
            chevronImg.preserveAspect = true;
            chevronImg.raycastTarget  = false;

            var toggleLabelGo = MakeRectChild(toggleGo, "ToggleLabel");
            var toggleLabelLe = toggleLabelGo.AddComponent<LayoutElement>();
            toggleLabelLe.flexibleWidth = 0;
            var toggleTmp = toggleLabelGo.AddComponent<TextMeshProUGUI>();
            toggleTmp.text      = "Details";
            toggleTmp.fontSize  = ToggleFontSize;
            toggleTmp.fontStyle = FontStyles.Normal;
            toggleTmp.color     = Styles.SecondaryText;
            toggleTmp.alignment = TextAlignmentOptions.MidlineLeft;
            toggleTmp.enableWordWrapping = false;
            toggleTmp.raycastTarget = false;

            // ── Verdict text ──────────────────────────────────────────────────
            var verdictGo = MakeRectChild(root, "VerdictText");
            var verdictLe = verdictGo.AddComponent<LayoutElement>();
            verdictLe.flexibleWidth = 1;
            var csf2 = verdictGo.AddComponent<ContentSizeFitter>();
            csf2.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var verdictTmp = verdictGo.AddComponent<TextMeshProUGUI>();
            verdictTmp.text      = "Verdict goes here.";
            verdictTmp.fontSize  = VerdictFontSize;
            verdictTmp.fontStyle = FontStyles.Normal;
            verdictTmp.color     = Styles.SecondaryText;
            verdictTmp.alignment = TextAlignmentOptions.TopLeft;
            verdictTmp.margin    = new Vector4(80, 0, 16, 8);
            verdictTmp.enableWordWrapping = true;
            verdictTmp.raycastTarget = false;

            // ── Details panel (hidden by default) ─────────────────────────────
            // A single TMP_Text node as child; hiding this node collapses the row.
            var detailsGo = MakeRectChild(root, "DetailsPanel");
            detailsGo.SetActive(false);
            var detailsLe = detailsGo.AddComponent<LayoutElement>();
            detailsLe.flexibleWidth = 1;
            var detailsCsf = detailsGo.AddComponent<ContentSizeFitter>();
            detailsCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var detailsTmp = detailsGo.AddComponent<TextMeshProUGUI>();
            detailsTmp.text      = "";
            detailsTmp.fontSize  = DetailsFontSize;
            detailsTmp.fontStyle = FontStyles.Italic;
            detailsTmp.color     = new Color32(0x9A, 0x9A, 0x9F, 0xFF);
            detailsTmp.alignment = TextAlignmentOptions.TopLeft;
            detailsTmp.margin    = new Vector4(80, 2, 16, 10);
            detailsTmp.enableWordWrapping = true;
            detailsTmp.raycastTarget = false;

            // ── Wire ResultsCriterionRow fields ───────────────────────────────
            var row   = root.AddComponent<ResultsCriterionRow>();
            var rowSo = new SerializedObject(row);
            rowSo.FindProperty("_badgeImage").objectReferenceValue        = badgeBgImg;
            rowSo.FindProperty("_scoreLabel").objectReferenceValue        = scoreTmp;
            rowSo.FindProperty("_nameLabel").objectReferenceValue         = nameTmp;
            rowSo.FindProperty("_detailsToggle").objectReferenceValue     = toggleBtn;
            rowSo.FindProperty("_detailsChevron").objectReferenceValue    = chevronGo.GetComponent<RectTransform>();
            rowSo.FindProperty("_verdictLabel").objectReferenceValue      = verdictTmp;
            rowSo.FindProperty("_detailsPanel").objectReferenceValue      = detailsGo;
            rowSo.FindProperty("_detailsLabel").objectReferenceValue      = detailsTmp;
            rowSo.ApplyModifiedPropertiesWithoutUndo();

            return root;
        }

        // ── ExpandableSection ─────────────────────────────────────────────────
        //
        // Header row (clickable Button) + collapsible body container. Mirrors
        // the CriterionRow Separator+Header pattern so the visual rhythm
        // inside the scroll list stays consistent.
        //
        //   Root  (VLG + ContentSizeFitter + ResultsExpandableSection)
        //    ├─ Separator           1 px Divider line
        //    ├─ HeaderRow           Button + HLG (chevron + title)
        //    │    ├─ ChevronLabel   TMP "▸" — flipped to "▾" when expanded
        //    │    └─ TitleLabel     TMP (bold, flex width)
        //    └─ Body                container (VLG + CSF, inactive by default)

        static GameObject BuildExpandableSection(Sprite _unused)
        {
            const int SectionHeaderHeight = 48;
            const int SectionHeaderFont   = 22;
            const int ChevronSize         = 20;
            const int BodyTopPad          = 4;
            const int BodyBottomPad       = 12;

            // Built-in Unity "DropdownArrow" sprite — points down by default;
            // ResultsExpandableSection rotates it -90° when collapsed.
            var dropdownArrow = UnityEditor.AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd");

            var root = new GameObject("ExpandableSection", typeof(RectTransform));

            var rootVlg = root.AddComponent<VerticalLayoutGroup>();
            rootVlg.padding               = new RectOffset(0, 0, 0, 4);
            rootVlg.spacing               = 0;
            rootVlg.childForceExpandWidth  = true;
            rootVlg.childForceExpandHeight = false;
            rootVlg.childControlWidth      = true;
            rootVlg.childControlHeight     = true;

            var rootCsf = root.AddComponent<ContentSizeFitter>();
            rootCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var rootLe = root.AddComponent<LayoutElement>();
            rootLe.flexibleWidth = 1;

            // Top divider.
            var sep = MakeRectChild(root, "Separator");
            var sepImg = sep.AddComponent<Image>();
            sepImg.color         = Styles.Divider;
            sepImg.raycastTarget = false;
            AddLE(sep, preferredHeight: DividerHeight);

            // ── Header row — the GameObject is both the layout container AND
            //    the Button click target. Image lives on this GameObject (full
            //    row width via HLG sizing of itself by parent VLG) so clicks
            //    hit the entire row. Adding Image to a child is the trap I
            //    hit before: HLG resized that child away and clicks missed.
            var headerGo = MakeRectChild(root, "HeaderRow");
            AddLE(headerGo, preferredHeight: SectionHeaderHeight);

            var headerBg = headerGo.AddComponent<Image>();
            headerBg.color         = new Color(1f, 1f, 1f, 0.02f); // barely-visible plate; raycast hits the whole row
            headerBg.raycastTarget = true;

            var toggleBtn = headerGo.AddComponent<Button>();
            toggleBtn.targetGraphic = headerBg;
            var colors = toggleBtn.colors;
            colors.normalColor      = new Color(1, 1, 1, 0.02f);
            colors.highlightedColor = new Color(1, 1, 1, 0.10f);
            colors.pressedColor     = new Color(1, 1, 1, 0.18f);
            colors.selectedColor    = colors.normalColor;
            toggleBtn.colors = colors;

            var headerHlg = headerGo.AddComponent<HorizontalLayoutGroup>();
            headerHlg.padding               = new RectOffset(16, 16, 6, 6);
            headerHlg.spacing               = 10;
            headerHlg.childAlignment        = TextAnchor.MiddleLeft;
            headerHlg.childForceExpandWidth  = false;
            headerHlg.childForceExpandHeight = true;
            headerHlg.childControlWidth      = true;
            headerHlg.childControlHeight     = true;

            // Chevron — Image with the built-in DropdownArrow sprite. Points
            // down (expanded) by default; ResultsExpandableSection sets the
            // RectTransform's localEulerAngles.z to -90 when collapsed so it
            // points right.
            var chevronGo = MakeRectChild(headerGo, "Chevron");
            var chevronLe = chevronGo.AddComponent<LayoutElement>();
            chevronLe.preferredWidth  = ChevronSize;
            chevronLe.preferredHeight = ChevronSize;
            chevronLe.flexibleWidth   = 0;
            chevronLe.flexibleHeight  = 0;
            var chevronImg = chevronGo.AddComponent<Image>();
            chevronImg.sprite         = dropdownArrow;
            chevronImg.color          = Styles.SecondaryText;
            chevronImg.preserveAspect = true;
            chevronImg.raycastTarget  = false;

            // Title.
            var titleGo = MakeRectChild(headerGo, "TitleLabel");
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.flexibleWidth = 1;
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text          = "Section";
            titleTmp.fontSize      = SectionHeaderFont;
            titleTmp.fontStyle     = FontStyles.Bold;
            titleTmp.color         = Styles.PrimaryText;
            titleTmp.alignment     = TextAlignmentOptions.MidlineLeft;
            titleTmp.enableWordWrapping = false;
            titleTmp.overflowMode  = TextOverflowModes.Ellipsis;
            titleTmp.raycastTarget = false;

            // Body container — hidden by default; ResultsExpandableSection
            // toggles its active state. Children appended at runtime by
            // ResultsPanelView.
            var bodyGo = MakeRectChild(root, "Body");
            bodyGo.SetActive(false);

            var bodyVlg = bodyGo.AddComponent<VerticalLayoutGroup>();
            bodyVlg.padding               = new RectOffset(0, 0, BodyTopPad, BodyBottomPad);
            bodyVlg.spacing               = 4;
            bodyVlg.childForceExpandWidth  = true;
            bodyVlg.childForceExpandHeight = false;
            bodyVlg.childControlWidth      = true;
            bodyVlg.childControlHeight     = true;

            var bodyCsf = bodyGo.AddComponent<ContentSizeFitter>();
            bodyCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var bodyLe = bodyGo.AddComponent<LayoutElement>();
            bodyLe.flexibleWidth = 1;

            // Wire the MonoBehaviour.
            var section = root.AddComponent<ResultsExpandableSection>();
            var so = new SerializedObject(section);
            so.FindProperty("_toggle").objectReferenceValue       = toggleBtn;
            so.FindProperty("_titleLabel").objectReferenceValue   = titleTmp;
            so.FindProperty("_chevronIcon").objectReferenceValue  = chevronGo.GetComponent<RectTransform>();
            so.FindProperty("_body").objectReferenceValue         = bodyGo;
            so.FindProperty("_bodyContent").objectReferenceValue  = bodyGo.GetComponent<RectTransform>();
            so.ApplyModifiedPropertiesWithoutUndo();

            return root;
        }

        // ── ResultsPanel ──────────────────────────────────────────────────────

        static GameObject BuildPanel(
            GameObject btnPrimary, GameObject btnSecondary,
            Sprite roundedSprite,
            ResultsCriterionRow rowPrefabComp,
            ResultsExpandableSection sectionPrefabComp)
        {
            // ── Root (canvas) ─────────────────────────────────────────────────
            var root   = new GameObject("ResultsPanel");
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

            var view = root.AddComponent<ResultsPanelView>();

            // ── Background ────────────────────────────────────────────────────
            var bg = MakeRectChild(root, "Background");
            StretchFill(bg);
            var bgImg = bg.AddComponent<Image>();
            bgImg.sprite        = roundedSprite;
            bgImg.type          = Image.Type.Sliced;
            bgImg.color         = Styles.PanelMuted;
            bgImg.raycastTarget = false;

            // ── Outer layout ──────────────────────────────────────────────────
            var layout = MakeRectChild(root, "Layout");
            StretchFill(layout);
            var outerVlg = layout.AddComponent<VerticalLayoutGroup>();
            outerVlg.padding               = new RectOffset(PaddingOuter, PaddingOuter, PaddingOuter, PaddingOuter);
            outerVlg.spacing               = ContentSpacing;
            outerVlg.childForceExpandWidth  = true;
            outerVlg.childForceExpandHeight = false;
            outerVlg.childControlWidth      = true;
            outerVlg.childControlHeight     = true;

            // ── Header (compact title bar) ────────────────────────────────────
            var headerGo  = MakeRectChild(layout, "Header");
            AddLE(headerGo, preferredHeight: HeaderHeight);
            var titleTmp = headerGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text      = "Your Performance Results";
            titleTmp.fontSize  = HeaderFontSize;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color     = Styles.PrimaryText;
            titleTmp.alignment = TextAlignmentOptions.MidlineLeft;
            titleTmp.enableWordWrapping = false;
            titleTmp.raycastTarget = false;

            // ── ScrollView (Lobby PresentationPickerPopup pattern) ────────────
            // Drag-anywhere-on-scroll works in VR only when the ScrollView itself
            // has a raycast-target Image AND the Viewport carries a near-zero-
            // alpha Image (alpha exactly 0 is skipped by the raycaster on some
            // configurations — alpha 0.01 is the proven Lobby trick). The
            // visible Scrollbar is intentionally omitted to keep the surface
            // entirely draggable (matches the slide picker UX).
            var scrollGo = MakeRectChild(layout, "ScrollView");
            var scrollLe = scrollGo.AddComponent<LayoutElement>();
            scrollLe.flexibleHeight = 1;
            scrollLe.flexibleWidth  = 1;

            var scrollBgImg = scrollGo.AddComponent<Image>();
            scrollBgImg.color         = Styles.Background; // darker inset under the rows
            scrollBgImg.raycastTarget = true;              // makes the entire surface ray-draggable

            var scrollRect = scrollGo.AddComponent<ScrollRect>();
            scrollRect.horizontal          = false;
            scrollRect.vertical            = true;
            scrollRect.scrollSensitivity   = 1f;  // matches Lobby PresentationPickerPopup; 40 felt jumpy under ray drag
            scrollRect.movementType        = ScrollRect.MovementType.Elastic;
            scrollRect.elasticity          = 0.1f;
            scrollRect.inertia             = true;
            scrollRect.decelerationRate    = 0.135f;
            scrollRect.verticalScrollbar             = null;
            scrollRect.verticalScrollbarVisibility   = ScrollRect.ScrollbarVisibility.Permanent;

            // Viewport: Mask + alpha-0.01 raycast-target Image (Lobby pattern).
            var viewportGo = MakeRectChild(scrollGo, "Viewport");
            var viewportRt = viewportGo.GetComponent<RectTransform>();
            viewportRt.anchorMin = new Vector2(0, 0);
            viewportRt.anchorMax = new Vector2(1, 1);
            viewportRt.offsetMin = Vector2.zero;
            viewportRt.offsetMax = Vector2.zero;

            var viewportMask = viewportGo.AddComponent<Mask>();
            viewportMask.showMaskGraphic = false;
            var viewportImg = viewportGo.AddComponent<Image>();
            viewportImg.color         = new Color(1f, 1f, 1f, 0.01f);
            viewportImg.raycastTarget = true;
            scrollRect.viewport = viewportRt;

            // Content (VLG + CSF drives total height)
            var contentGo = MakeRectChild(viewportGo, "Content");
            var contentRt = contentGo.GetComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0, 1);
            contentRt.anchorMax = new Vector2(1, 1);
            contentRt.pivot     = new Vector2(0.5f, 1);
            contentRt.offsetMin = Vector2.zero;
            contentRt.offsetMax = Vector2.zero;

            var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
            contentVlg.padding               = new RectOffset(ScrollPadding, ScrollPadding, ScrollPadding, ScrollPadding);
            contentVlg.spacing               = 0;
            contentVlg.childForceExpandWidth  = true;
            contentVlg.childForceExpandHeight = false;
            contentVlg.childControlWidth      = true;
            contentVlg.childControlHeight     = true;

            var contentCsf = contentGo.AddComponent<ContentSizeFitter>();
            contentCsf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scrollRect.content = contentRt;

            // ── Footer (two fixed-width buttons, centered) ────────────────────
            var footerGo = MakeRectChild(layout, "Footer");
            AddLE(footerGo, preferredHeight: FooterHeight);
            var footerHlg = footerGo.AddComponent<HorizontalLayoutGroup>();
            footerHlg.spacing               = 16;
            footerHlg.childAlignment        = TextAnchor.MiddleCenter;
            footerHlg.childForceExpandWidth  = false;
            footerHlg.childForceExpandHeight = false;
            footerHlg.childControlWidth      = false;
            footerHlg.childControlHeight     = false;

            // Save as PDF button (secondary style)
            var saveBtnInst = (GameObject)PrefabUtility.InstantiatePrefab(btnSecondary, footerGo.transform);
            saveBtnInst.name = "SavePdfButton";
            SetFixedButton(saveBtnInst, FooterButtonWidth, FooterHeight);
            SetButtonLabel(saveBtnInst, "Save as PDF", ButtonFontSize);
            var saveBtn = saveBtnInst.GetComponent<Button>();

            // Go to Lobby button (primary style)
            var lobbyBtnInst = (GameObject)PrefabUtility.InstantiatePrefab(btnPrimary, footerGo.transform);
            lobbyBtnInst.name = "GoToLobbyButton";
            SetFixedButton(lobbyBtnInst, FooterButtonWidth, FooterHeight);
            SetButtonLabel(lobbyBtnInst, "Go to Lobby", ButtonFontSize);
            var lobbyBtn = lobbyBtnInst.GetComponent<Button>();

            // ── Wire ResultsPanelView fields ──────────────────────────────────
            var viewSo = new SerializedObject(view);
            viewSo.FindProperty("_canvasGroup").objectReferenceValue        = cg;
            viewSo.FindProperty("_scrollRect").objectReferenceValue         = scrollRect;
            viewSo.FindProperty("_scrollContent").objectReferenceValue      = contentGo.transform;
            viewSo.FindProperty("_criterionRowPrefab").objectReferenceValue = rowPrefabComp;
            viewSo.FindProperty("_expandableSectionPrefab").objectReferenceValue = sectionPrefabComp;
            viewSo.FindProperty("_savePdfButton").objectReferenceValue      = saveBtn;
            viewSo.FindProperty("_goToLobbyButton").objectReferenceValue    = lobbyBtn;
            viewSo.ApplyModifiedPropertiesWithoutUndo();

            return root;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static GameObject MakeRectChild(GameObject parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        static void StretchFill(GameObject go)
        {
            var r    = go.GetComponent<RectTransform>();
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.zero;
            r.offsetMax = Vector2.zero;
        }

        static void AddLE(GameObject go, float preferredHeight)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredHeight = preferredHeight;
            le.flexibleWidth   = 1;
        }

        static void SetFixedButton(GameObject go, float width, float height)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredWidth  = width;
            le.preferredHeight = height;
            le.minWidth        = width;
            le.minHeight       = height;
            le.flexibleWidth   = 0;
            le.flexibleHeight  = 0;
        }

        static void SetButtonLabel(GameObject buttonGo, string text, int fontSize = ButtonFontSize)
        {
            var label = buttonGo.GetComponentInChildren<TMP_Text>();
            if (label == null) return;
            label.text               = text;
            label.fontSize           = fontSize;
            label.enableAutoSizing   = false;
            label.enableWordWrapping = false;
            label.overflowMode       = TextOverflowModes.Ellipsis;
        }
    }
}
