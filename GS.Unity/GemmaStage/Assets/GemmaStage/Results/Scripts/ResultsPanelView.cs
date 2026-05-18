using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using GemmaStage.Core;
using GemmaStage.Session;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Results
{
    // Pure view for the Results floating panel. Receives a DeepDiveResult and
    // populates a scrollable list of criterion swimlane rows plus an optional
    // Main Idea Comparator section (visible only when ground truth was present).
    // Animation timing matches EvaluationPanelView / FinalQaPanelView.
    //
    // Wired by ResultsPanelBuilder; driven by ResultsController.
    [DisallowMultipleComponent]
    public sealed class ResultsPanelView : MonoBehaviour
    {
        const float ShowDuration = 0.3f;
        const float HideDuration = 0.12f;
        const float ShowStartScale = 0.5f;
        const float ShowPeakScale = 1.18f;
        const float ShowPeakAt = 0.6f;
        const float HideEndScale = 0.94f;

        // Wired by builder.
        [SerializeField] CanvasGroup              _canvasGroup;
        [SerializeField] ScrollRect               _scrollRect;
        [SerializeField] Transform                _scrollContent;
        [SerializeField] ResultsCriterionRow      _criterionRowPrefab;
        [SerializeField] ResultsExpandableSection _expandableSectionPrefab;
        [SerializeField] Button                   _savePdfButton;
        [SerializeField] Button                   _goToLobbyButton;

        public event Action OnSavePdfPressed;
        public event Action OnGoToLobbyPressed;

        Coroutine _animRoutine;
        Vector3   _baseScale;

        void Awake()
        {
            _baseScale = transform.localScale;
            if (_canvasGroup != null) _canvasGroup.alpha = 0f;
            gameObject.SetActive(false);

            if (_savePdfButton    != null) _savePdfButton.onClick.AddListener(()  => OnSavePdfPressed?.Invoke());
            if (_goToLobbyButton  != null) _goToLobbyButton.onClick.AddListener(() => OnGoToLobbyPressed?.Invoke());
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Populate(SessionResultsPayload payload)
        {
            if (payload == null || _scrollContent == null || _criterionRowPrefab == null) return;

            ClearContent();

            var result = payload.DeepDive;
            var d      = payload.Details;
            if (result != null)
            {
                AddCriterionRow("Main Idea Clarity",       result.MainIdeaClarity,      null);
                AddCriterionRow("Structure",                result.Structure,            FormatScoreSliceDetails(d?.Structure));
                AddCriterionRow("Consistency & Focus",      result.ConsistencyFocus,     FormatScoreSliceDetails(d?.ConsistencyFocus));
                AddCriterionRow("Support & Justification",  result.SupportJustification, FormatScoreSliceDetails(d?.SupportJustification));
                AddCriterionRow("Language Quality",          result.LanguageQuality,      FormatLanguageDetails(d?.LanguageQuality));
                AddCriterionRow("Emotional Delivery",        result.EmotionalDelivery,    FormatEmotionDetails(d?.EmotionalDelivery));
                AddCriterionRow("Q&A Handling",              result.QaHandling,           FormatQaDetails(d?.QaHandling));

                if (result.Comparator != null)
                    BuildComparatorSection(result.Comparator);
            }

            // ── Stage 1 expandable sections ──────────────────────────────────
            if (!string.IsNullOrWhiteSpace(payload.InferredMainIdeaFromTranscript))
                BuildTextSection("Idea understanding from transcript", payload.InferredMainIdeaFromTranscript);

            if (!string.IsNullOrWhiteSpace(payload.AudienceThesis))
                BuildTextSection("Audience-call idea understanding", payload.AudienceThesis);

            if (payload.Transcript != null && payload.Transcript.Count > 0)
                BuildTranscriptSection(payload.Transcript);

            // Layout rebuild is deferred to Show() — ForceUpdateCanvases has no
            // effect while the GameObject is inactive (Awake sets it inactive).
        }

        public void Show()
        {
            gameObject.SetActive(true);
            // Force ContentSizeFitter to compute row heights now that the canvas
            // is active. Must happen after SetActive(true) — inactive canvases
            // are excluded from ForceUpdateCanvases.
            Canvas.ForceUpdateCanvases();
            if (_scrollContent != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(_scrollContent as RectTransform);
            if (_scrollRect != null) _scrollRect.verticalNormalizedPosition = 1f;

            if (_animRoutine != null) StopCoroutine(_animRoutine);
            _animRoutine = StartCoroutine(ShowRoutine());
        }

        public void Hide()
        {
            if (!gameObject.activeSelf) return;
            if (_animRoutine != null) StopCoroutine(_animRoutine);
            _animRoutine = StartCoroutine(HideRoutine());
        }

        // ── Content builders ─────────────────────────────────────────────────

        void ClearContent()
        {
            for (int i = _scrollContent.childCount - 1; i >= 0; i--)
                Destroy(_scrollContent.GetChild(i).gameObject);
        }

        void AddCriterionRow(string categoryName, ResultCriterion criterion, string detailsText)
        {
            var row = Instantiate(_criterionRowPrefab, _scrollContent);
            row.Populate(categoryName, criterion, detailsText);
        }

        // ── DeepDive per-role detail formatters (Stage 2) ────────────────────
        //
        // Each returns a TMP rich-text string for ResultsCriterionRow's details
        // toggle. Returning null/empty hides the Details button. Layout is
        // plain multi-line text — keeps the panel rendering deterministic in
        // VR and avoids spawning child layouts inside a row.

        static string FormatScoreSliceDetails(DeepDiveScoreSliceDetails d)
        {
            if (d == null) return null;
            var sb = new StringBuilder();
            sb.Append("<b>Computed:</b> ").Append(d.ComputedValue).Append(" (").Append(d.ComputedLabel).Append(")\n\n");
            if (d.Rows.Count > 0)
            {
                sb.Append("<b>Chunks</b>\n");
                for (int i = 0; i < d.Rows.Count; i++)
                {
                    var r = d.Rows[i];
                    sb.Append("  #").Append(r.Index).Append("  <color=#9A9A9F>[")
                      .Append(r.Label).Append("]</color>  ").Append(r.Retelling).Append('\n');
                }
            }
            return sb.ToString().TrimEnd();
        }

        static string FormatLanguageDetails(DeepDiveLanguageDetails d)
        {
            if (d == null) return null;
            var sb = new StringBuilder();
            sb.Append("<b>Computed:</b> ").Append(d.ComputedValue).Append(" (").Append(d.ComputedLabel).Append(")\n");
            if (!string.IsNullOrWhiteSpace(d.DistributionOverall))
                sb.Append("<b>Overall:</b> ").Append(d.DistributionOverall).Append('\n');
            sb.Append('\n');
            AppendDistribution(sb, d.Distribution);
            if (d.WeakSlices.Count > 0)
            {
                sb.Append("\n<b>Weak chunks</b>\n");
                for (int i = 0; i < d.WeakSlices.Count; i++)
                {
                    var s = d.WeakSlices[i];
                    sb.Append("  <color=#9A9A9F>#").Append(s.Sequence).Append("</color>  ").Append(s.Text).Append('\n');
                }
            }
            AppendDynamicsNotes(sb, "Grammar notes", d.Notes);
            return sb.ToString().TrimEnd();
        }

        // Q&A rounds — transcript-style table, one round per multi-line cell:
        //   [Live · resolved]   Q: <question>
        //                       A: <answer>
        // Phase chip is color-coded (Live=Primary, Final=Warning); resolution
        // chip is colored by outcome (resolved=success, not answered=error).
        static string FormatQaDetails(DeepDiveQaDetails d)
        {
            if (d == null || d.Spans == null || d.Spans.Count == 0) return null;
            var sb = new StringBuilder();
            int live  = 0;
            int final = 0;
            for (int i = 0; i < d.Spans.Count; i++)
            {
                if (d.Spans[i].Phase == "Final") final++; else live++;
            }
            sb.Append("<b>Rounds:</b> ").Append(live).Append(" live, ").Append(final).Append(" final\n\n");

            for (int i = 0; i < d.Spans.Count; i++)
            {
                var s = d.Spans[i];
                bool resolved = s.Resolution == "resolved";
                string phaseColor      = s.Phase == "Final" ? "#F5A623" : "#7B61FF";
                string resolutionColor = resolved ? "#3FB87B" : "#E15B64";

                sb.Append("<color=").Append(phaseColor).Append("><b>[").Append(s.Phase).Append("]</b></color>  ");
                sb.Append("<color=").Append(resolutionColor).Append(">· ").Append(s.Resolution).Append("</color>\n");
                sb.Append("  <b>Q:</b> ").Append(s.Question).Append('\n');
                sb.Append("  <b>A:</b> ").Append(s.Answer).Append("\n\n");
            }
            return sb.ToString().TrimEnd();
        }

        static string FormatEmotionDetails(DeepDiveEmotionDetails d)
        {
            if (d == null) return null;
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(d.DistributionOverall))
                sb.Append("<b>Overall:</b> ").Append(d.DistributionOverall).Append("\n\n");
            AppendDistribution(sb, d.Distribution);
            AppendDynamicsNotes(sb, "Emotion notes", d.Notes);
            return sb.ToString().TrimEnd();
        }

        static void AppendDistribution(StringBuilder sb, IReadOnlyList<DeepDiveDistributionBucket> dist)
        {
            if (dist == null || dist.Count == 0) return;
            sb.Append("<b>Distribution</b>\n");
            for (int i = 0; i < dist.Count; i++)
            {
                var b = dist[i];
                sb.Append("  <color=#9A9A9F>").Append(b.Label).Append("</color>: ")
                  .Append(b.Count).Append('\n');
            }
        }

        static void AppendDynamicsNotes(StringBuilder sb, string heading, IReadOnlyList<DeepDiveDynamicsNote> notes)
        {
            if (notes == null || notes.Count == 0) return;
            sb.Append("\n<b>").Append(heading).Append("</b>\n");
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                sb.Append("  <color=#9A9A9F>[").Append(n.Tag).Append("]</color>  ")
                  .Append(n.Transcript).Append('\n');
            }
        }

        // ── Expandable sections (Stage 1) ─────────────────────────────────

        // Single-text expandable: header + one wrapped TMP body block.
        void BuildTextSection(string title, string body)
        {
            var section = SpawnExpandable(title);
            if (section == null) return;

            var bodyGo = MakeTextLabel(
                section.BodyContent, "BodyText", body,
                fontSize: Styles.Type.BodyPt,
                style:    FontStyles.Normal,
                color:    Styles.PrimaryText,
                margin:   new Vector4(16, 8, 16, 12));
            // TMP exposes its preferred height via ILayoutElement; the section
            // body's VLG+CSF reads it directly. No explicit LayoutElement needed.
            _ = bodyGo;
        }

        // Transcript expandable: header + one row per chunk. Each row is a
        // single multi-line TMP cell formatted as
        //   [mm:ss] text · emotion · grammar
        void BuildTranscriptSection(IReadOnlyList<TranscriptChunkRecord> rows)
        {
            var section = SpawnExpandable($"Transcript ({rows.Count})");
            if (section == null) return;

            for (int i = 0; i < rows.Count; i++)
                MakeTranscriptRow(section.BodyContent, rows[i]);
        }

        ResultsExpandableSection SpawnExpandable(string title)
        {
            if (_expandableSectionPrefab == null)
            {
                Debug.LogWarning($"[ResultsPanelView] ExpandableSection prefab missing — '{title}' skipped.");
                return null;
            }
            var section = Instantiate(_expandableSectionPrefab, _scrollContent);
            section.Init(title);
            return section;
        }

        // One transcript row. Single multi-line cell so long text wraps cleanly
        // and there's no fixed-column truncation. Emotion/Grammar are inline at
        // the end in a muted color.
        void MakeTranscriptRow(Transform parent, TranscriptChunkRecord row)
        {
            var go = new GameObject("TranscriptRow", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            var sb  = new StringBuilder();
            sb.Append("<b><color=#7B61FF>[");
            sb.Append(FormatOffset(row.Offset));
            sb.Append("]</color></b>  ");
            sb.Append(row.Text);
            sb.Append("\n<size=20><color=#9A9A9F>· ");
            sb.Append(string.IsNullOrEmpty(row.Emotion) ? "—" : row.Emotion);
            sb.Append("  · ");
            sb.Append(string.IsNullOrEmpty(row.Grammar) ? "—" : row.Grammar);
            sb.Append("</color></size>");
            tmp.text              = sb.ToString();
            tmp.fontSize          = Styles.Type.BodyPt;
            tmp.color             = Styles.PrimaryText;
            tmp.alignment         = TextAlignmentOptions.TopLeft;
            tmp.enableWordWrapping = true;
            tmp.margin            = new Vector4(16, 4, 16, 4);
            tmp.raycastTarget     = false;

            var csf = go.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var le = go.AddComponent<LayoutElement>();
            le.flexibleWidth = 1;
        }

        static string FormatOffset(TimeSpan offset)
        {
            if (offset.TotalHours >= 1)
                return $"{(int)offset.TotalHours:D2}:{offset.Minutes:D2}:{offset.Seconds:D2}";
            return $"{(int)offset.TotalMinutes:D2}:{offset.Seconds:D2}";
        }

        // Builds the "Main Idea Analysis" section at runtime — one-off so no
        // dedicated prefab is needed. Structure:
        //   SectionHeader
        //   InfoRow  × 3  (anchor thesis / audience thesis / comparison)
        //   RecallRow      (recall percentage)
        //   ClaimsHeader
        //   ClaimRow × N
        void BuildComparatorSection(ResultComparatorData cmp)
        {
            // ── Outer divider ────────────────────────────────────────────────
            MakeDivider(_scrollContent, height: 2f, color: Styles.Divider);

            // ── Section header ───────────────────────────────────────────────
            var headerGo = MakeTextLabel(
                _scrollContent, "ComparatorHeader",
                "Main Idea Analysis",
                fontSize: Styles.Type.HeadingPt,
                style: FontStyles.Bold,
                color: Styles.PrimaryText,
                margin: new Vector4(16, 12, 16, 4));
            AddLayoutElement(headerGo, preferredHeight: 48f);

            // ── Thesis rows ──────────────────────────────────────────────────
            MakeInfoRow(_scrollContent, "Anchor Thesis:",   cmp.AnchorThesis);
            MakeInfoRow(_scrollContent, "Audience Thesis:", cmp.AudienceThesis);
            MakeInfoRow(_scrollContent, "Comparison:",      cmp.ThesisComparison);

            // ── Recall row ───────────────────────────────────────────────────
            int pct = Mathf.RoundToInt((float)cmp.Recall * 100);
            Color32 recallColor = cmp.Recall >= 0.65 ? Styles.IconSuccess
                                : cmp.Recall >= 0.50 ? Styles.Warning
                                : Styles.Error;
            MakeInfoRow(_scrollContent, "Recall:", $"{pct}%", valueColor: recallColor);

            // ── Claims header ────────────────────────────────────────────────
            if (cmp.ClaimCoverages.Count > 0)
            {
                var claimsHdr = MakeTextLabel(
                    _scrollContent, "ClaimsHeader",
                    "Claim Coverage",
                    fontSize: Styles.Type.BodyPt,
                    style: FontStyles.Bold,
                    color: Styles.SecondaryText,
                    margin: new Vector4(16, 8, 16, 2));
                AddLayoutElement(claimsHdr, preferredHeight: 38f);

                // ── One row per claim ────────────────────────────────────────
                foreach (var claim in cmp.ClaimCoverages)
                    MakeClaimRow(_scrollContent, claim);
            }

            // Bottom breathing room
            MakeDivider(_scrollContent, height: 16f, color: new Color32(0, 0, 0, 0));
        }

        // ── Runtime UI helpers ────────────────────────────────────────────────

        // A label + value pair stacked vertically inside a small VLG container.
        void MakeInfoRow(Transform parent, string label, string value,
                         Color32? valueColor = null)
        {
            var container = new GameObject("InfoRow_" + label, typeof(RectTransform));
            container.transform.SetParent(parent, false);

            var vlg = container.AddComponent<VerticalLayoutGroup>();
            vlg.padding               = new RectOffset(16, 16, 2, 4);
            vlg.spacing               = 2;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth     = true;
            vlg.childControlHeight    = true;

            var csf = container.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var le = container.AddComponent<LayoutElement>();
            le.flexibleWidth = 1;

            // Label line
            var labelGo  = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(container.transform, false);
            var labelTmp  = labelGo.AddComponent<TextMeshProUGUI>();
            labelTmp.text      = label;
            labelTmp.fontSize  = Styles.Type.CaptionPt;
            labelTmp.fontStyle = FontStyles.Bold;
            labelTmp.color     = Styles.SecondaryText;
            labelTmp.enableWordWrapping = false;
            labelTmp.raycastTarget = false;

            // Value line
            var valueGo  = new GameObject("Value", typeof(RectTransform));
            valueGo.transform.SetParent(container.transform, false);
            var valueTmp  = valueGo.AddComponent<TextMeshProUGUI>();
            valueTmp.text      = value ?? "";
            valueTmp.fontSize  = Styles.Type.BodyPt;
            valueTmp.fontStyle = FontStyles.Normal;
            valueTmp.color     = valueColor ?? Styles.PrimaryText;
            valueTmp.enableWordWrapping = true;
            valueTmp.raycastTarget = false;
        }

        // One claim coverage row: [Icon] Claim text  (+ optional evidence)
        void MakeClaimRow(Transform parent, ResultClaimCoverage claim)
        {
            var container = new GameObject("ClaimRow", typeof(RectTransform));
            container.transform.SetParent(parent, false);

            var hlg = container.AddComponent<HorizontalLayoutGroup>();
            hlg.padding               = new RectOffset(24, 16, 2, 2);
            hlg.spacing               = 8;
            hlg.childAlignment        = TextAnchor.UpperLeft;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.childControlWidth     = true;
            hlg.childControlHeight    = true;

            var csf = container.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var le = container.AddComponent<LayoutElement>();
            le.flexibleWidth = 1;

            // Coverage icon (colored ● dot)
            var iconGo  = new GameObject("Icon", typeof(RectTransform));
            iconGo.transform.SetParent(container.transform, false);
            var iconLe  = iconGo.AddComponent<LayoutElement>();
            iconLe.preferredWidth  = 22;
            iconLe.preferredHeight = 22;
            iconLe.flexibleWidth   = 0;
            var iconTmp = iconGo.AddComponent<TextMeshProUGUI>();
            iconTmp.text      = "●";
            iconTmp.fontSize  = 18;
            iconTmp.color     = CoverageColor(claim.Coverage);
            iconTmp.alignment = TextAlignmentOptions.TopLeft;
            iconTmp.raycastTarget = false;

            // Claim text (+ evidence on second line if present)
            var claimGo  = new GameObject("ClaimText", typeof(RectTransform));
            claimGo.transform.SetParent(container.transform, false);
            var claimLe  = claimGo.AddComponent<LayoutElement>();
            claimLe.flexibleWidth = 1;
            var claimTmp = claimGo.AddComponent<TextMeshProUGUI>();

            var sb = new StringBuilder(claim.Claim ?? "");
            if (!string.IsNullOrWhiteSpace(claim.Evidence))
            {
                sb.Append("\n<size=20><color=#9A9A9F><i>");
                sb.Append(claim.Evidence);
                sb.Append("</i></color></size>");
            }
            claimTmp.text      = sb.ToString();
            claimTmp.fontSize  = Styles.Type.BodyPt;
            claimTmp.color     = Styles.PrimaryText;
            claimTmp.enableWordWrapping = true;
            claimTmp.raycastTarget = false;
        }

        static Color32 CoverageColor(string coverage) => coverage switch
        {
            "yes"     => Styles.IconSuccess,
            "partial" => Styles.Warning,
            _         => Styles.Error,
        };

        static GameObject MakeTextLabel(Transform parent, string goName, string text,
                                         float fontSize, FontStyles style, Color32 color,
                                         Vector4 margin)
        {
            var go  = new GameObject(goName, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp  = go.AddComponent<TextMeshProUGUI>();
            tmp.text              = text;
            tmp.fontSize          = fontSize;
            tmp.fontStyle         = style;
            tmp.color             = color;
            tmp.enableWordWrapping = true;
            tmp.margin            = margin;
            tmp.raycastTarget     = false;
            return go;
        }

        static void MakeDivider(Transform parent, float height, Color32 color)
        {
            var go = new GameObject("Divider", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color         = color;
            img.raycastTarget = false;
            var le  = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth   = 1;
        }

        static void AddLayoutElement(GameObject go, float preferredHeight)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.preferredHeight = preferredHeight;
            le.flexibleWidth   = 1;
        }

        // ── Animations ────────────────────────────────────────────────────────

        IEnumerator ShowRoutine()
        {
            if (_canvasGroup != null) _canvasGroup.alpha = 0f;
            transform.localScale = _baseScale * ShowStartScale;

            float t = 0f;
            while (t < ShowDuration)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / ShowDuration);
                if (_canvasGroup != null) _canvasGroup.alpha = Mathf.Clamp01(k * 3f);

                float sk;
                if (k < ShowPeakAt)
                {
                    float k1    = k / ShowPeakAt;
                    float eased = 1f - (1f - k1) * (1f - k1) * (1f - k1);
                    sk = Mathf.Lerp(ShowStartScale, ShowPeakScale, eased);
                }
                else
                {
                    float k2    = (k - ShowPeakAt) / (1f - ShowPeakAt);
                    float eased = k2 < 0.5f ? 2f * k2 * k2 : 1f - Mathf.Pow(-2f * k2 + 2f, 2f) * 0.5f;
                    sk = Mathf.Lerp(ShowPeakScale, 1f, eased);
                }
                transform.localScale = _baseScale * sk;
                yield return null;
            }

            if (_canvasGroup != null)
            {
                _canvasGroup.alpha         = 1f;
                _canvasGroup.interactable  = true;
                _canvasGroup.blocksRaycasts = true;
            }
            transform.localScale = _baseScale;
            _animRoutine         = null;
        }

        IEnumerator HideRoutine()
        {
            float startAlpha = _canvasGroup != null ? _canvasGroup.alpha : 1f;
            float startSk    = transform.localScale.x / _baseScale.x;
            if (_canvasGroup != null) { _canvasGroup.interactable = false; _canvasGroup.blocksRaycasts = false; }

            float t = 0f;
            while (t < HideDuration)
            {
                t += Time.unscaledDeltaTime;
                float k     = Mathf.Clamp01(t / HideDuration);
                float eased = k * k;
                if (_canvasGroup != null) _canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, eased);
                transform.localScale = _baseScale * Mathf.Lerp(startSk, HideEndScale, eased);
                yield return null;
            }

            if (_canvasGroup != null) _canvasGroup.alpha = 0f;
            transform.localScale = _baseScale;
            _animRoutine         = null;
            gameObject.SetActive(false);
        }
    }
}
