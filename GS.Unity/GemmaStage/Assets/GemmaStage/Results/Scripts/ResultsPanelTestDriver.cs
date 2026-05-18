using System;
using System.IO;
using GemmaStage.Results;
using GemmaStage.Session;
using TMPro;
using UnityEngine;

// Test driver for the Results floating panel. Spawns five panel instances
// in an arc in front of the camera — one per evaluation state — so all
// states can be inspected and interacted with in VR simultaneously.
//
// Usage:
//   1. Open ResultsPanelTest.unity (alongside Shared.unity for XR input).
//   2. Select the TestDriver GameObject → assign ResultsPanel.prefab.
//   3. Press Play.
//
// Panel prefab path:
//   Assets/GemmaStage/Results/UI/Prefabs/ResultsPanel.prefab
public sealed class ResultsPanelTestDriver : MonoBehaviour
{
    [SerializeField] ResultsPanelView panelPrefab;

    [Tooltip("Arc radius from camera (metres). Default 3 m is comfortable reading distance in VR.")]
    [SerializeField] float arcRadius = 3f;

    [Tooltip("Total arc span in degrees. 80° = 40° either side of camera forward.")]
    [SerializeField] float arcDegrees = 80f;

    [Tooltip("World-space Y of panel centres.")]
    [SerializeField] float panelHeight = 1.5f;

    void Start()
    {
        if (panelPrefab == null)
        {
            Debug.LogError("[ResultsPanelTestDriver] panelPrefab not assigned. " +
                           "Assign Assets/GemmaStage/Results/UI/Prefabs/ResultsPanel.prefab.");
            return;
        }

        var states = new (string label, SessionResultsPayload payload)[]
        {
            ("1 — Perfect Score / No Ground Truth",    SessionResultsPayload.ForDev(MakePerfectNoGT())),
            ("2 — Strong Performance / GT Recall 83%", SessionResultsPayload.ForDev(MakeStrongWithGT())),
            ("3 — Average / GT Recall 38%",            SessionResultsPayload.ForDev(MakeAverageWithGT())),
            ("4 — Poor / GT Recall 6%",                SessionResultsPayload.ForDev(MakePoorWithGT())),
            ("5 — Mixed / No GT / Q&A Active",         SessionResultsPayload.ForDev(MakeMixedNoGT())),
        };

        var cam    = Camera.main;
        var origin = cam != null ? cam.transform.position : new Vector3(0, 1.5f, 0);

        int   count    = states.Length;
        float startDeg = -arcDegrees / 2f;
        float stepDeg  = count > 1 ? arcDegrees / (count - 1) : 0f;

        for (int i = 0; i < count; i++)
        {
            float angleDeg = startDeg + i * stepDeg;
            float rad      = angleDeg * Mathf.Deg2Rad;

            var dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            var pos = origin + dir * arcRadius;
            pos.y   = panelHeight;

            // Initial rotation: face toward camera (LookAtCamera on prefab keeps this live).
            var toCamera = Vector3.ProjectOnPlane(origin - pos, Vector3.up).normalized;
            var rot      = toCamera.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(toCamera)
                : Quaternion.identity;

            var panel = Instantiate(panelPrefab);
            panel.transform.SetPositionAndRotation(pos, rot);
            panel.Populate(states[i].payload);
            panel.Show();

            // Wire Save-as-PDF for testing: render a minimal PDF from the dev
            // payload via ResultsTestPdfExporter and open it through the OS
            // shell. Captured locals avoid the foreach-style closure trap.
            var capturedLabel   = states[i].label;
            var capturedPayload = states[i].payload;
            panel.OnSavePdfPressed += () => HandleTestSavePdf(capturedLabel, capturedPayload);

            // Small floating label above each panel to identify the state.
            SpawnLabel(states[i].label, pos + Vector3.up * 0.36f, rot);
        }
    }

    // ── Save-as-PDF test handler ─────────────────────────────────────────────

    static void HandleTestSavePdf(string label, SessionResultsPayload payload)
    {
        try
        {
            var dir       = Path.Combine(Application.persistentDataPath, "SessionExports");
            var safeLabel = SanitizeFileName(label);
            var stamp     = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var path      = Path.Combine(dir, $"test-results-{safeLabel}-{stamp}.pdf");

            ResultsTestPdfExporter.Export(path, label, payload);
            Debug.Log($"[ResultsPanelTestDriver] Wrote PDF: {path}");

            var psi = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ResultsPanelTestDriver] PDF export failed: {ex}");
        }
    }

    static string SanitizeFileName(string s)
    {
        if (string.IsNullOrEmpty(s)) return "panel";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        // Trim leading numbers/spaces/em-dashes for a cleaner filename.
        return s.Replace(' ', '_').Replace("—", "-");
    }

    // ── Floating label ────────────────────────────────────────────────────────

    static void SpawnLabel(string text, Vector3 position, Quaternion rotation)
    {
        var root   = new GameObject("Label", typeof(RectTransform));
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var rt        = root.GetComponent<RectTransform>();
        rt.sizeDelta  = new Vector2(760f, 60f);
        rt.localScale = Vector3.one * 0.00075f;
        root.transform.SetPositionAndRotation(position, rotation);

        var textGo  = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(root.transform, false);
        var textRt  = textGo.GetComponent<RectTransform>();
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = Vector2.zero;
        textRt.offsetMax = Vector2.zero;

        var tmp             = textGo.AddComponent<TextMeshProUGUI>();
        tmp.text            = text;
        tmp.fontSize        = 26f;
        tmp.fontStyle       = FontStyles.Bold;
        tmp.color           = Color.white;
        tmp.alignment       = TextAlignmentOptions.Center;
        tmp.raycastTarget   = false;
    }

    // ── Test states ───────────────────────────────────────────────────────────

    // State 1 — All five core criteria at max. Both optional criteria N/A.
    // No ground-truth doc → no comparator section.
    static DeepDiveResult MakePerfectNoGT() => new DeepDiveResult(
        new ResultCriterion(5, "The core message was stated in the opening sentence and never wavered. A model of communicative precision."),
        new ResultCriterion(5, "Textbook arc: crisp introduction, logical step-by-step development, decisive conclusion. Canonical ordering."),
        new ResultCriterion(5, "Exceptional focus. Every sentence served the central argument. Zero tangents."),
        new ResultCriterion(5, "Every major claim was backed by concrete evidence, statistics, or illustrative examples. Nothing was asserted without support."),
        new ResultCriterion(5, "Fluent, grammatically impeccable, and naturally engaging from start to finish."),
        new ResultCriterion(0, "N/A — no expressive moments were detected in this session."),
        new ResultCriterion(0, "N/A — no Q&A rounds were recorded."),
        null);

    // State 2 — Strong across the board. Ground truth present, high recall (83%).
    // Audience captured nearly all anchor claims.
    static DeepDiveResult MakeStrongWithGT() => new DeepDiveResult(
        new ResultCriterion(5, "Audience comprehension was near-total. The comparator confirms 83% claim recall with strong thesis alignment."),
        new ResultCriterion(4, "Clear intro and conclusion. One development chunk felt slightly out of sequence but didn't disrupt comprehension."),
        new ResultCriterion(4, "Very focused. One minor tangent recovered within about 30 seconds."),
        new ResultCriterion(4, "Solid evidence throughout. One claim relied on audience familiarity rather than explicit support."),
        new ResultCriterion(5, "Excellent grammar, varied vocabulary, and a naturally authoritative register."),
        new ResultCriterion(4, "Energy well-calibrated to topic weight. Genuine enthusiasm during the key thesis statement."),
        new ResultCriterion(3, "Three Q&A rounds: two cleanly resolved, one answer drifted before landing on the point."),
        new ResultComparatorData(
            anchorThesis:     "Adequate sleep is essential for memory consolidation, immune health, and metabolic regulation.",
            audienceThesis:   "Sleep is vital for memory and immunity, and sleep deprivation harms both.",
            thesisComparison: "Strong capture of the dual-benefit thesis. The metabolic regulation angle was the only notable gap.",
            claimCoverages: new ResultClaimCoverage[]
            {
                new("Sleep consolidates declarative memory during deep sleep stages.",  "yes",     "paragraph explicitly mentions memory consolidation"),
                new("Chronic sleep deprivation suppresses T-cell production.",          "yes",     "immune function captured directly"),
                new("Metabolic disorders rise significantly with under-7-hour sleep.",  "no",      null),
                new("Adults perform optimally with 7–9 hours per night.",               "partial", "hours mentioned but not linked to performance outcomes"),
                new("REM sleep is specifically tied to emotional memory processing.",   "yes",     "REM stage referenced in context of emotional recall"),
                new("Sleep debt cannot be fully recovered in a single night.",          "partial", "hinted at but not explicitly stated"),
            },
            recall: 0.833));

    // State 3 — Average performance. Ground truth present, medium recall (38%).
    // Audience got the gist but missed important specifics.
    static DeepDiveResult MakeAverageWithGT() => new DeepDiveResult(
        new ResultCriterion(3, "The central theme emerged by mid-talk. An attentive listener would identify the topic but it was never stated upfront."),
        new ResultCriterion(3, "Intro and conclusion present but indistinct. Development chunks were recognisable but their structural role was often ambiguous."),
        new ResultCriterion(3, "Mostly on-topic. Two tangents introduced and never explicitly tied back to the central argument."),
        new ResultCriterion(3, "Mixed support: some claims well-evidenced, others asserted. Roughly an even split."),
        new ResultCriterion(4, "Generally fluent with a few grammatical slips. Register appropriate for the audience."),
        new ResultCriterion(2, "Mostly flat delivery. Some moments of emphasis but energy didn't sustain through the key claims."),
        new ResultCriterion(3, "Two Q&A rounds: one resolved cleanly, one remained vague despite a follow-up attempt."),
        new ResultComparatorData(
            anchorThesis:     "Remote work boosts productivity and well-being when paired with structured async communication.",
            audienceThesis:   "Remote work can be effective and employees generally prefer it.",
            thesisComparison: "High-level sentiment captured but the structured async angle was entirely absent. Productivity evidence was vague.",
            claimCoverages: new ResultClaimCoverage[]
            {
                new("Remote workers report 13% higher productivity on average (Stanford).", "partial", "productivity mentioned without supporting data"),
                new("Async communication cuts meeting overhead by 40%.",                    "no",      null),
                new("Employee well-being improves with schedule autonomy.",                 "yes",     "well-being and flexibility captured explicitly"),
                new("Structured check-in cadences prevent remote isolation.",               "no",      null),
            },
            recall: 0.375));

    // State 4 — Poor performance. Ground truth present, very low recall (6%).
    // Audience retained almost nothing of the intended content.
    static DeepDiveResult MakePoorWithGT() => new DeepDiveResult(
        new ResultCriterion(1, "The main idea was never articulated. A listener could not reliably summarise the talk's purpose even after the conclusion."),
        new ResultCriterion(2, "A rough ending was detectable but no introduction was present. Sections felt disconnected from each other."),
        new ResultCriterion(1, "Significant topic drift. Subjects shifted without signposting; prior threads were abandoned mid-development."),
        new ResultCriterion(2, "Almost all claims were unsupported assertions. One example was offered but was tangential."),
        new ResultCriterion(3, "Occasional grammatical errors and unclear phrasing, though not frequent enough to break comprehension entirely."),
        new ResultCriterion(0, "N/A — no expressive moments detected."),
        new ResultCriterion(0, "N/A — no Q&A rounds recorded."),
        new ResultComparatorData(
            anchorThesis:     "Blockchain enables trustless, decentralised financial transactions that eliminate banking intermediaries.",
            audienceThesis:   "Blockchain is a technology used for transactions.",
            thesisComparison: "Only the most surface-level description was retained. The core value proposition was entirely missing from the audience understanding.",
            claimCoverages: new ResultClaimCoverage[]
            {
                new("Blockchain uses a distributed ledger verified by consensus.",         "no",      null),
                new("Smart contracts execute automatically when conditions are met.",       "no",      null),
                new("Decentralisation eliminates single points of failure.",               "no",      null),
                new("Transaction costs drop significantly without banking intermediaries.", "partial", "cost mentioned vaguely; intermediary removal absent"),
                new("Bitcoin settled $10T in transactions in 2023.",                       "no",      null),
                new("Ethereum processes approximately 1M transactions per day.",           "no",      null),
                new("Permissioned blockchains are used in enterprise supply chains.",      "no",      null),
                new("Smart contract audits reduce exploit risk by ~60%.",                  "no",      null),
            },
            recall: 0.0625));

    // State 5 — Mixed scores across all seven criteria including Q&A.
    // No ground truth → no comparator section.
    static DeepDiveResult MakeMixedNoGT() => new DeepDiveResult(
        new ResultCriterion(3, "The message was present but required the listener to synthesise it from scattered hints. An explicit early thesis would have helped significantly."),
        new ResultCriterion(4, "Good structural clarity. Intro present, conclusion strong. One mid-section transition was weak."),
        new ResultCriterion(4, "Largely consistent. One sub-topic felt like a digression but was brief and recoverable."),
        new ResultCriterion(2, "Claims were frequently made without evidence. The few examples provided were relevant but far too sparse."),
        new ResultCriterion(4, "Good language overall. A few informal slips that didn't meaningfully impede comprehension."),
        new ResultCriterion(3, "Moderate engagement. Authentic enthusiasm on the central claim; delivery elsewhere was fairly flat."),
        new ResultCriterion(4, "Four Q&A rounds recorded. Three clearly resolved. One answer wandered before addressing the core concern."),
        null);
}
