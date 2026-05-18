using GemmaStage.Session.Clarification;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.MainIdeaComparator;
using GemmaStage.Session.Stores;
using GemmaStage.Session.TranscriptSummarizer;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GemmaStage.Session.DeepDive;

// Packages runtime signals into the seven per-sub-role inputs the
// DeepDiveCoordinator consumes. All compression (distributions,
// dominant labels, phase windowing, etc.) happens here so the
// per-sub-role conversations only see structured, ready-to-render data.
public static class DeepDiveInputBuilder
{
    private static readonly string[] ConfusionPhases =
    {
        "early", "early_mid", "middle", "late_mid", "late",
    };

    public static DeepDiveSubInputs Build(
        TranscriptSummarizerReduceOutput transcriptSummary,
        string audienceSideRecall,
        IReadOnlyList<TranscriptSummarizerMapOutput> mapOutputs,
        RetellingsHistory retellingsHistory,
        MetricsStore metricsStore,
        TranscriptStore transcriptStore,
        QaRoundsHistory qaRoundsHistory,
        ClarificationResult? clarification,
        MainIdeaComparatorOutput? mainIdeaComparator)
    {
        Guard.NotNull(transcriptSummary);
        Guard.NotNull(mapOutputs);
        Guard.NotNull(retellingsHistory);
        Guard.NotNull(metricsStore);
        Guard.NotNull(transcriptStore);
        Guard.NotNull(qaRoundsHistory);

        var reflections = retellingsHistory.Snapshot();
        var metrics = metricsStore.Snapshot();
        var transcripts = transcriptStore.Snapshot();
        var qaRounds = qaRoundsHistory.Snapshot();

        var confusionDynamics = ComputeConfusionDynamics(reflections);
        var emotionDynamics = ComputeEmotionDynamics(metrics, transcripts);
        var grammarDynamics = ComputeGrammarDynamics(metrics, transcripts);
        var computedLanguageQuality = ComputeLanguageQuality(grammarDynamics.distribution);

        var structureRows = BuildStructureChunkRows(mapOutputs);
        var consistencyRows = BuildConsistencyChunkRows(mapOutputs);
        var supportRows = BuildSupportChunkRows(mapOutputs);
        var languageWeakSlices = BuildLanguageWeakSlices(metrics, transcripts);
        var qaSpans = BuildQaSpans(qaRounds, reflections, transcripts, clarification);
        var shortInput = BuildShortInput(mapOutputs, transcripts, confusionDynamics);

        var computedStructure = ComputeStructureScore(structureRows);
        var computedConsistency = ComputeConsistencyScore(consistencyRows);
        var computedSupport = ComputeSupportScore(supportRows);
        var computedMainIdea = mainIdeaComparator is not null
            ? ComputeMainIdeaClarityScore(mainIdeaComparator.Recall)
            : null;

        var mainIdeaInput = new MainIdeaClarity.MainIdeaClarityInput(
            transcriptSummary.InferredMainIdeaFromTranscript,
            audienceSideRecall,
            confusionDynamics,
            mainIdeaComparator,
            computedMainIdea);

        var structureInput = new Structure.StructureInput(structureRows, confusionDynamics, computedStructure);
        var consistencyInput = new ConsistencyFocus.ConsistencyFocusInput(consistencyRows, confusionDynamics, computedConsistency);
        var supportInput = new SupportJustification.SupportJustificationInput(supportRows, computedSupport);
        var languageInput = new LanguageQuality.LanguageQualityInput(languageWeakSlices, computedLanguageQuality, grammarDynamics);
        var emotionalInput = new EmotionalDelivery.EmotionalDeliveryInput(
            transcriptSummary.InferredMainIdeaFromTranscript,
            emotionDynamics);
        var qaInput = new QaHandling.QaHandlingInput(qaSpans);

        return new DeepDiveSubInputs(
            mainIdeaInput,
            structureInput,
            consistencyInput,
            supportInput,
            languageInput,
            emotionalInput,
            qaInput,
            shortInput);
    }

    // Concatenated raw transcript + chunk retellings + confusion dynamics.
    // Used by the short-transcript branch of Structure / Consistency / Support
    // when fewer than 3 chunks make the deterministic ladders unreliable.
    internal static DeepDiveShortInput BuildShortInput(
        IReadOnlyList<TranscriptSummarizerMapOutput> mapOutputs,
        IReadOnlyList<TranscriptEntry> transcripts,
        IReadOnlyList<ConfusionDynamicsPoint> confusionDynamics)
    {
        var rawText = transcripts.Count == 0
            ? string.Empty
            : string.Join(
                " ",
                transcripts
                    .Where(t => !string.IsNullOrWhiteSpace(t.Text))
                    .OrderBy(t => t.Sequence)
                    .Select(t => t.Text));

        var retellings = mapOutputs.Count == 0
            ? Array.Empty<string>()
            : mapOutputs.Select(m => m.Retelling).ToArray();

        return new DeepDiveShortInput(rawText, retellings, confusionDynamics);
    }

    // Deterministic Language Quality verdict from the grammar-label
    // distribution, per the rule:
    //   excellent - no `poor` chunks, AND excellent * 2 > good
    //   poor      - 4 * poor > good + excellent
    //   otherwise - good
    // `moderate` chunks are not weighted in this rule (they neither push
    // toward "excellent" nor "poor").
    //
    // Score mapping (DeepDive 1-5 scale):
    //   excellent -> 5, good -> 4, poor -> 2.
    internal static ComputedLanguageQuality ComputeLanguageQuality(
        IReadOnlyDictionary<string, int> distribution)
    {
        Guard.NotNull(distribution);

        var poor = distribution.TryGetValue("poor", out var p) ? p : 0;
        var good = distribution.TryGetValue("good", out var g) ? g : 0;
        var excellent = distribution.TryGetValue("excellent", out var e) ? e : 0;

        if (poor == 0 && excellent * 2 > good)
        {
            return new ComputedLanguageQuality(5, "excellent");
        }

        if (4 * poor > good + excellent)
        {
            return new ComputedLanguageQuality(2, "poor");
        }

        return new ComputedLanguageQuality(4, "good");
    }

    internal static ComputedScore ComputeStructureScore(IReadOnlyList<StructureChunkRow> rows)
    {
        if (rows.Count == 0) return new ComputedScore(3, "no chunks");
        bool hasIntro = rows.Any(r => r.StructureLabel == "intro");
        bool hasConclusion = rows.Any(r => r.StructureLabel == "conclusion");
        bool hasUnclear = rows.Any(r => r.StructureLabel == "unclear");
        if (!hasIntro && !hasConclusion) return new ComputedScore(1, "no intro or conclusion");
        if (!hasIntro || !hasConclusion) return new ComputedScore(2, "missing intro or conclusion");
        if (hasUnclear) return new ComputedScore(3, "intro + conclusion present, unclear chunks");
        int lastIntroIdx = rows.Select((r, i) => (r, i)).Where(x => x.r.StructureLabel == "intro").Max(x => x.i);
        int firstConclusionIdx = rows.Select((r, i) => (r, i)).Where(x => x.r.StructureLabel == "conclusion").Min(x => x.i);
        return lastIntroIdx < firstConclusionIdx
            ? new ComputedScore(5, "intro → development → conclusion")
            : new ComputedScore(4, "intro + conclusion present, non-canonical order");
    }

    internal static ComputedScore ComputeConsistencyScore(IReadOnlyList<ConsistencyChunkRow> rows)
    {
        if (rows.Count == 0) return new ComputedScore(3, "no chunks");
        int consistent = rows.Count(r => r.ConsistencyLabel == "consistent");
        int minor = rows.Count(r => r.ConsistencyLabel == "minor_drift");
        int major = rows.Count(r => r.ConsistencyLabel == "major_drift");
        // Most-severe first so each condition is mutually exclusive.
        if (major + 2 * minor > 2 * consistent) return new ComputedScore(1, $"major({major})+2*minor({minor}) > 2*consistent({consistent})");
        if (major + 2 * minor > consistent)     return new ComputedScore(2, $"major({major})+2*minor({minor}) > consistent({consistent})");
        if (major == 0 && consistent > 3 * minor) return new ComputedScore(5, $"consistent({consistent}) > 3*minor({minor}), no major");
        if (major == 0 && consistent > 2 * minor) return new ComputedScore(4, $"consistent({consistent}) > 2*minor({minor}), no major");
        return new ComputedScore(3, $"major({major}), consistent({consistent}) <= 2*minor({minor})");
    }

    internal static ComputedScore ComputeSupportScore(IReadOnlyList<SupportChunkRow> rows)
    {
        if (rows.Count == 0) return new ComputedScore(3, "no chunks");
        // `none` is merged into `weak` (treated as unsupported).
        int s = rows.Count(r => r.SupportLabel == "strong");
        int m = rows.Count(r => r.SupportLabel == "moderate");
        int w = rows.Count(r => r.SupportLabel == "weak" || r.SupportLabel == "none");
        if (s == 0 && m == 0)   return new ComputedScore(1, "all weak");
        if (s + m < w)          return new ComputedScore(2, $"strong+moderate({s + m}) < weak({w})");
        if (s > 2 * (m + w))    return new ComputedScore(5, $"strong({s}) > 2*(moderate+weak)({2 * (m + w)})");
        if (s >= m + w)         return new ComputedScore(4, $"strong({s}) >= moderate+weak({m + w})");
        return new ComputedScore(3, $"middle: strong={s}, moderate={m}, weak={w}");
    }

    internal static ComputedScore ComputeMainIdeaClarityScore(double recall)
    {
        int value = recall < 0.30 ? 1 : recall < 0.50 ? 2 : recall < 0.65 ? 3 : recall < 0.80 ? 4 : 5;
        return new ComputedScore(value, $"recall={recall:0.00}");
    }

    internal static IReadOnlyList<ConfusionDynamicsPoint> ComputeConfusionDynamics(
        IReadOnlyList<RetellingHistoryEntry> reflections)
    {
        if (reflections.Count == 0)
        {
            return Array.Empty<ConfusionDynamicsPoint>();
        }

        var scores = reflections
            .Select(r => (r.StateAfter.ConfusionScore ?? InquirerConfusionScore.Low).ToString().ToLowerInvariant())
            .ToList();

        if (scores.Count <= ConfusionPhases.Length)
        {
            return scores
                .Select((s, i) => new ConfusionDynamicsPoint(
                    ConfusionPhases[Math.Min(i, ConfusionPhases.Length - 1)], s))
                .ToList();
        }

        var result = new List<ConfusionDynamicsPoint>(ConfusionPhases.Length);
        for (int i = 0; i < ConfusionPhases.Length; i++)
        {
            int start = i * scores.Count / ConfusionPhases.Length;
            int end = (i + 1) * scores.Count / ConfusionPhases.Length;
            var window = scores.Skip(start).Take(end - start).ToList();
            var dominant = window.GroupBy(x => x).OrderByDescending(g => g.Count()).First().Key;
            result.Add(new ConfusionDynamicsPoint(ConfusionPhases[i], dominant));
        }

        return result;
    }

    internal static EmotionDynamics ComputeEmotionDynamics(
        IReadOnlyList<MetricEntry> metrics,
        IReadOnlyList<TranscriptEntry> transcripts)
    {
        var valid = metrics.Where(m => !string.IsNullOrEmpty(m.Emotion)).ToList();
        if (valid.Count == 0)
        {
            return new EmotionDynamics(
                "calm",
                new Dictionary<string, int>(),
                Array.Empty<EmotionDynamicsNote>());
        }

        var distribution = valid.GroupBy(m => m.Emotion!).ToDictionary(g => g.Key, g => g.Count());
        var overall = distribution.OrderByDescending(kvp => kvp.Value).First().Key;

        var transcriptBySequence = transcripts.ToDictionary(t => t.Sequence, t => t.Text);
        var notes = valid
            .Where(m => m.Emotion != "calm")
            .Select(m => (m.Emotion, Text: transcriptBySequence.GetValueOrDefault(m.Sequence)))
            .Where(x => !string.IsNullOrEmpty(x.Text))
            .Select(x => new EmotionDynamicsNote(x.Emotion!, x.Text!))
            .ToList();

        return new EmotionDynamics(overall, distribution, notes);
    }

    internal static GrammarDynamics ComputeGrammarDynamics(
        IReadOnlyList<MetricEntry> metrics,
        IReadOnlyList<TranscriptEntry> transcripts)
    {
        var valid = metrics.Where(m => !string.IsNullOrEmpty(m.Grammar)).ToList();
        if (valid.Count == 0)
        {
            return new GrammarDynamics(
                "good",
                new Dictionary<string, int>(),
                Array.Empty<GrammarDynamicsNote>());
        }

        var distribution = valid.GroupBy(m => m.Grammar!).ToDictionary(g => g.Key, g => g.Count());
        var overall = distribution.OrderByDescending(kvp => kvp.Value).First().Key;

        var transcriptBySequence = transcripts.ToDictionary(t => t.Sequence, t => t.Text);
        var notes = valid
            .Where(m => m.Grammar == "poor")
            .Select(m => (m.Grammar, Text: transcriptBySequence.GetValueOrDefault(m.Sequence)))
            .Where(x => !string.IsNullOrEmpty(x.Text))
            .Take(5)
            .Select(x => new GrammarDynamicsNote(x.Grammar!, x.Text!))
            .ToList();

        return new GrammarDynamics(overall, distribution, notes);
    }

    internal static IReadOnlyList<StructureChunkRow> BuildStructureChunkRows(
        IReadOnlyList<TranscriptSummarizerMapOutput> mapOutputs)
    {
        if (mapOutputs.Count == 0) return Array.Empty<StructureChunkRow>();
        var rows = new StructureChunkRow[mapOutputs.Count];
        for (int i = 0; i < mapOutputs.Count; i++)
        {
            rows[i] = new StructureChunkRow(i + 1, mapOutputs[i].Retelling, mapOutputs[i].Structure);
        }
        return rows;
    }

    internal static IReadOnlyList<ConsistencyChunkRow> BuildConsistencyChunkRows(
        IReadOnlyList<TranscriptSummarizerMapOutput> mapOutputs)
    {
        if (mapOutputs.Count == 0) return Array.Empty<ConsistencyChunkRow>();
        var rows = new ConsistencyChunkRow[mapOutputs.Count];
        for (int i = 0; i < mapOutputs.Count; i++)
        {
            rows[i] = new ConsistencyChunkRow(i + 1, mapOutputs[i].Retelling, mapOutputs[i].Consistency);
        }
        return rows;
    }

    internal static IReadOnlyList<SupportChunkRow> BuildSupportChunkRows(
        IReadOnlyList<TranscriptSummarizerMapOutput> mapOutputs)
    {
        if (mapOutputs.Count == 0) return Array.Empty<SupportChunkRow>();
        var rows = new SupportChunkRow[mapOutputs.Count];
        for (int i = 0; i < mapOutputs.Count; i++)
        {
            rows[i] = new SupportChunkRow(i + 1, mapOutputs[i].Retelling, mapOutputs[i].Support);
        }
        return rows;
    }

    // Slices of the raw transcript for chunks tagged grammar=poor or moderate.
    // The Language Quality sub-role uses these to ground its verdict in
    // concrete chunk excerpts (the score itself is deterministic).
    internal static IReadOnlyList<TranscriptSlice> BuildLanguageWeakSlices(
        IReadOnlyList<MetricEntry> metrics,
        IReadOnlyList<TranscriptEntry> transcripts)
    {
        if (metrics.Count == 0 || transcripts.Count == 0) return Array.Empty<TranscriptSlice>();

        var transcriptBySequence = transcripts.ToDictionary(t => t.Sequence, t => t.Text);
        var slices = new List<TranscriptSlice>();
        foreach (var m in metrics)
        {
            if (m.Grammar != "poor" && m.Grammar != "moderate") continue;
            if (transcriptBySequence.TryGetValue(m.Sequence, out var text) && !string.IsNullOrWhiteSpace(text))
            {
                slices.Add(new TranscriptSlice(m.Sequence, text));
            }
        }
        return slices;
    }

    // Walks closed Q&A rounds chronologically, slices the answer text by
    // joining transcript-chunk timestamps to the round window, and decides
    // resolution by checking the resolved-concern id union (live inquirer
    // reflections + final Clarification).
    internal static IReadOnlyList<QaSpan> BuildQaSpans(
        IReadOnlyList<QaRoundEntry> qaRounds,
        IReadOnlyList<RetellingHistoryEntry> reflections,
        IReadOnlyList<TranscriptEntry> transcripts,
        ClarificationResult? clarification)
    {
        if (qaRounds.Count == 0) return Array.Empty<QaSpan>();

        var resolvedIds = new HashSet<long>();
        foreach (var r in reflections)
        {
            foreach (var removed in r.Reflection.RemovedConcerns)
            {
                if (removed.Cause == RemovedConcernCause.Resolved)
                {
                    resolvedIds.Add(removed.Id);
                }
            }
        }
        if (clarification is not null)
        {
            foreach (var id in clarification.ResolvedIds)
            {
                resolvedIds.Add(id);
            }
        }

        var spans = new List<QaSpan>(qaRounds.Count);
        foreach (var round in qaRounds)
        {
            var answerText = SliceAnswerText(transcripts, round.OpenedAt, round.ClosedAt);
            var resolution = resolvedIds.Contains(round.ConcernId) ? QaResolution.Resolved : QaResolution.NotAnswered;
            spans.Add(new QaSpan(round.Phase, round.QuestionText, answerText, resolution));
        }
        return spans;
    }

    private static string SliceAnswerText(
        IReadOnlyList<TranscriptEntry> transcripts,
        DateTimeOffset openedAt,
        DateTimeOffset closedAt)
    {
        if (transcripts.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var t in transcripts)
        {
            if (t.Timestamp < openedAt || t.Timestamp > closedAt) continue;
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t.Text);
        }
        return sb.ToString();
    }
}
