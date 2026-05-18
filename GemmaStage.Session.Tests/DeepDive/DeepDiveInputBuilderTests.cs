using System;
using System.Collections.Generic;
using GemmaStage.Session.DeepDive;
using GemmaStage.Session.Stores;
using GemmaStage.Session.Inquirer;

namespace GemmaStage.Session.Tests.DeepDive;

internal static class DeepDiveInputBuilderTests
{
    public static void Run()
    {
        ComputeConfusionDynamics_WithFewerThanFivePoints_ReturnsDirectlyWithPhases();
        ComputeConfusionDynamics_WithMoreThanFivePoints_CompressesToFiveWindows();
        ComputeEmotionDynamics_AdmitsEnthusiasticAlongsideTenseAndUncertain();
        ComputeGrammarDynamics_CalculatesOverallAndDistribution_AndCollectsOnlyPoorNotes();
        ComputeLanguageQuality_AppliesUserSpecifiedRule();
        BuildLanguageWeakSlices_AdmitsBothPoorAndModerate();
        BuildQaSpans_JoinsRoundsAndResolutionFromReflectionsAndClarification();
    }

    private static void ComputeLanguageQuality_AppliesUserSpecifiedRule()
    {
        // "excellent": no poor AND excellent * 2 > good
        // The run-007 case: { good: 33, excellent: 21 }. excellent*2 = 42 > 33, no poor → excellent.
        var run007 = new Dictionary<string, int> { ["good"] = 33, ["excellent"] = 21 };
        var r = DeepDiveInputBuilder.ComputeLanguageQuality(run007);
        AssertEx.Equal("excellent", r.label, "run-007 distribution must resolve to excellent");
        AssertEx.Equal(5, r.value, "excellent must map to score 5");

        // Boundary: 2*excellent == good is NOT excellent (rule is strict >).
        var boundary = new Dictionary<string, int> { ["good"] = 10, ["excellent"] = 5 };
        AssertEx.Equal("good", DeepDiveInputBuilder.ComputeLanguageQuality(boundary).label,
            "2*excellent == good must fall through to good");

        // Any "poor" present blocks the excellent path even if numbers favour it.
        var anyPoorBlocksExcellent = new Dictionary<string, int>
        {
            ["poor"] = 1, ["good"] = 1, ["excellent"] = 100,
        };
        AssertEx.Equal("good", DeepDiveInputBuilder.ComputeLanguageQuality(anyPoorBlocksExcellent).label,
            "Any single 'poor' chunk blocks the excellent verdict regardless of excellent count (4*1 = 4 not > 1+100, so it falls through to good — not poor)");

        // "poor": 4*poor > good + excellent
        var clearlyPoor = new Dictionary<string, int> { ["poor"] = 5, ["good"] = 3, ["excellent"] = 1 };
        var rp = DeepDiveInputBuilder.ComputeLanguageQuality(clearlyPoor);
        AssertEx.Equal("poor", rp.label, "4*poor (20) > good+excellent (4) → poor");
        AssertEx.Equal(2, rp.value, "poor must map to score 2");

        // "moderate" is unweighted in the rule — many moderates do not push toward poor.
        var lotsOfModerate = new Dictionary<string, int>
        {
            ["moderate"] = 100, ["good"] = 1,
        };
        AssertEx.Equal("good", DeepDiveInputBuilder.ComputeLanguageQuality(lotsOfModerate).label,
            "Moderate is unweighted; default verdict is good");

        // Empty distribution falls through to good.
        AssertEx.Equal("good", DeepDiveInputBuilder.ComputeLanguageQuality(new Dictionary<string, int>()).label,
            "Empty distribution must default to good (4)");
    }

    private static void ComputeConfusionDynamics_WithFewerThanFivePoints_ReturnsDirectlyWithPhases()
    {
        var reflections = new List<RetellingHistoryEntry>
        {
            CreateReflection(InquirerConfusionScore.Low),
            CreateReflection(InquirerConfusionScore.Medium),
            CreateReflection(InquirerConfusionScore.High)
        };

        var result = DeepDiveInputBuilder.ComputeConfusionDynamics(reflections);

        AssertEx.Equal(3, result.Count, "Should return exactly 3 points");
        AssertEx.Equal("early", result[0].phase, "1st phase");
        AssertEx.Equal("low", result[0].value, "1st value");
        AssertEx.Equal("early_mid", result[1].phase, "2nd phase");
        AssertEx.Equal("medium", result[1].value, "2nd value");
        AssertEx.Equal("middle", result[2].phase, "3rd phase");
        AssertEx.Equal("high", result[2].value, "3rd value");
    }

    private static void ComputeConfusionDynamics_WithMoreThanFivePoints_CompressesToFiveWindows()
    {
        var reflections = new List<RetellingHistoryEntry>
        {
            // window 1 (0..2): Low, Low -> dominant Low
            CreateReflection(InquirerConfusionScore.Low),
            CreateReflection(InquirerConfusionScore.Low), 

            // window 2 (2..4): Medium, Medium -> dominant Medium
            CreateReflection(InquirerConfusionScore.Medium),
            CreateReflection(InquirerConfusionScore.Medium),

            // window 3 (4..6): VeryHigh, VeryHigh -> dominant VeryHigh
            CreateReflection(InquirerConfusionScore.VeryHigh),
            CreateReflection(InquirerConfusionScore.VeryHigh), 

            // window 4 (6..8): Medium, High -> tie, tie broken by first encountered in GroupBy -> usually Medium
            CreateReflection(InquirerConfusionScore.Medium),
            CreateReflection(InquirerConfusionScore.High),

            // window 5 (8..11): Low, High, Low -> dominant Low
            CreateReflection(InquirerConfusionScore.Low),
            CreateReflection(InquirerConfusionScore.High),
            CreateReflection(InquirerConfusionScore.Low) 
        };

        var result = DeepDiveInputBuilder.ComputeConfusionDynamics(reflections);

        AssertEx.Equal(5, result.Count, "Should compress to 5 points");
        AssertEx.Equal("early", result[0].phase, "1st phase");
        AssertEx.Equal("low", result[0].value, "1st value");
        
        AssertEx.Equal("early_mid", result[1].phase, "2nd phase");
        AssertEx.Equal("medium", result[1].value, "2nd value");
        
        AssertEx.Equal("middle", result[2].phase, "3rd phase");
        AssertEx.Equal("veryhigh", result[2].value, "3rd value");
        
        AssertEx.Equal("late_mid", result[3].phase, "4th phase");
        
        AssertEx.Equal("late", result[4].phase, "5th phase");
        AssertEx.Equal("low", result[4].value, "5th value");
    }

    private static void ComputeEmotionDynamics_AdmitsEnthusiasticAlongsideTenseAndUncertain()
    {
        var metrics = new List<MetricEntry>
        {
            new MetricEntry(1, DateTimeOffset.UtcNow, "normal", "calm", null, true),
            new MetricEntry(2, DateTimeOffset.UtcNow, "normal", "tense", null, true),
            new MetricEntry(3, DateTimeOffset.UtcNow, "normal", "uncertain", null, true),
            new MetricEntry(4, DateTimeOffset.UtcNow, "normal", "enthusiastic", null, true),
            new MetricEntry(5, DateTimeOffset.UtcNow, "normal", "tense", null, true),
        };

        var transcripts = new List<TranscriptEntry>
        {
            new TranscriptEntry(1, DateTimeOffset.UtcNow, "Hello there."),
            new TranscriptEntry(2, DateTimeOffset.UtcNow, "This is tense 1"),
            new TranscriptEntry(3, DateTimeOffset.UtcNow, "This is uncertain 1"),
            new TranscriptEntry(4, DateTimeOffset.UtcNow, "Yay everyone!"),
            new TranscriptEntry(5, DateTimeOffset.UtcNow, "This is tense 2"),
        };

        var result = DeepDiveInputBuilder.ComputeEmotionDynamics(metrics, transcripts);

        AssertEx.Equal("tense", result.overall, "Overall emotion is the most-frequent label");
        AssertEx.Equal(2, result.distribution["tense"], "Tense count");
        AssertEx.Equal(1, result.distribution["uncertain"], "Uncertain count");
        AssertEx.Equal(1, result.distribution["calm"], "Calm count");
        AssertEx.Equal(1, result.distribution["enthusiastic"], "Enthusiastic count");

        // Notes include every non-calm chunk in source order: tense, uncertain, enthusiastic, tense.
        AssertEx.Equal(4, result.notes.Count, "All non-calm chunks appear as notes; no cap");
        AssertEx.Equal("tense", result.notes[0].emotion, "First note");
        AssertEx.Equal("This is tense 1", result.notes[0].transcript, "First note transcript");
        AssertEx.Equal("uncertain", result.notes[1].emotion, "Second note");
        AssertEx.Equal("enthusiastic", result.notes[2].emotion, "Third note now includes enthusiastic");
        AssertEx.Equal("Yay everyone!", result.notes[2].transcript, "Enthusiastic note transcript");
        AssertEx.Equal("tense", result.notes[3].emotion, "Fourth note");
    }

    private static void BuildLanguageWeakSlices_AdmitsBothPoorAndModerate()
    {
        var metrics = new List<MetricEntry>
        {
            new MetricEntry(1, DateTimeOffset.UtcNow, "normal", null, "good", true),
            new MetricEntry(2, DateTimeOffset.UtcNow, "normal", null, "poor", true),
            new MetricEntry(3, DateTimeOffset.UtcNow, "normal", null, "moderate", true),
            new MetricEntry(4, DateTimeOffset.UtcNow, "normal", null, "excellent", true),
            new MetricEntry(5, DateTimeOffset.UtcNow, "normal", null, "moderate", true),
        };
        var transcripts = new List<TranscriptEntry>
        {
            new TranscriptEntry(1, DateTimeOffset.UtcNow, "Perfect grammar."),
            new TranscriptEntry(2, DateTimeOffset.UtcNow, "Me is hungry."),
            new TranscriptEntry(3, DateTimeOffset.UtcNow, "He don't know."),
            new TranscriptEntry(4, DateTimeOffset.UtcNow, "Outstanding."),
            new TranscriptEntry(5, DateTimeOffset.UtcNow, "She go to store."),
        };

        var slices = DeepDiveInputBuilder.BuildLanguageWeakSlices(metrics, transcripts);

        AssertEx.Equal(3, slices.Count, "Slices include poor + both moderates, excluding good and excellent");
        AssertEx.Equal("Me is hungry.", slices[0].Text, "First slice is the poor chunk");
        AssertEx.Equal("He don't know.", slices[1].Text, "Second slice is the first moderate chunk");
        AssertEx.Equal("She go to store.", slices[2].Text, "Third slice is the second moderate chunk");
    }

    private static void BuildQaSpans_JoinsRoundsAndResolutionFromReflectionsAndClarification()
    {
        var t0 = DateTimeOffset.Parse("2026-04-30T10:00:00+00:00");

        // Two rounds: round 1 (concern 100) gets resolved by inquirer reflection;
        // round 2 (concern 200) is never resolved, so it must come back NotAnswered.
        var rounds = new List<QaRoundEntry>
        {
            new QaRoundEntry(QaPhase.Live, 100, "Question A", t0, t0.AddSeconds(20)),
            new QaRoundEntry(QaPhase.Final, 200, "Question B", t0.AddMinutes(5), t0.AddMinutes(5).AddSeconds(15)),
        };

        var idea = new GemmaStage.Session.IdeaReflector.IdeaReflectorReflection(
            "topic", "retelling", "main idea understanding");
        var reflections = new List<RetellingHistoryEntry>
        {
            new RetellingHistoryEntry(
                1, t0.AddSeconds(25), "brief",
                Array.Empty<RawContentSegment>(),
                idea,
                new InquirerReflection(
                    Array.Empty<InquirerConcern>(),
                    new[] { new RemovedConcern(100, RemovedConcernCause.Resolved, "ok") }),
                new InquirerStateSnapshot("topic", "idea", InquirerConfusionScore.Low, Array.Empty<InquirerConcern>())),
        };

        // The answer text for round 1 should pick up only the chunks
        // timestamped inside the [OpenedAt, ClosedAt] window.
        var transcripts = new List<TranscriptEntry>
        {
            new TranscriptEntry(1, t0.AddSeconds(-30), "before any round"),
            new TranscriptEntry(2, t0.AddSeconds(10), "this is the live answer"),
            new TranscriptEntry(3, t0.AddMinutes(5).AddSeconds(5), "this is the final answer"),
            new TranscriptEntry(4, t0.AddMinutes(10), "after every round"),
        };

        var spans = DeepDiveInputBuilder.BuildQaSpans(rounds, reflections, transcripts, clarification: null);

        AssertEx.Equal(2, spans.Count, "BuildQaSpans must emit one span per closed round");
        AssertEx.Equal(QaPhase.Live, spans[0].Phase, "First span is the live round");
        AssertEx.Equal("this is the live answer", spans[0].AnswerText, "Answer text picks chunks inside the round window");
        AssertEx.Equal(QaResolution.Resolved, spans[0].Resolution, "Concern 100 was resolved by a subsequent inquirer reflection");
        AssertEx.Equal(QaPhase.Final, spans[1].Phase, "Second span is the final round");
        AssertEx.Equal("this is the final answer", spans[1].AnswerText, "Final round answer text");
        AssertEx.Equal(QaResolution.NotAnswered, spans[1].Resolution, "Concern 200 was never resolved -> NotAnswered");
    }

    private static void ComputeGrammarDynamics_CalculatesOverallAndDistribution_AndCollectsOnlyPoorNotes()
    {
        var metrics = new List<MetricEntry>
        {
            new MetricEntry(1, DateTimeOffset.UtcNow, "normal", null, "good", true),
            new MetricEntry(2, DateTimeOffset.UtcNow, "normal", null, "poor", true),
            new MetricEntry(3, DateTimeOffset.UtcNow, "normal", null, "good", true),
            new MetricEntry(4, DateTimeOffset.UtcNow, "normal", null, "moderate", true),
            new MetricEntry(5, DateTimeOffset.UtcNow, "normal", null, "poor", true)
        };

        var transcripts = new List<TranscriptEntry>
        {
            new TranscriptEntry(1, DateTimeOffset.UtcNow, "Perfect grammar."),
            new TranscriptEntry(2, DateTimeOffset.UtcNow, "Me is hungry."),
            new TranscriptEntry(3, DateTimeOffset.UtcNow, "I agree."),
            new TranscriptEntry(4, DateTimeOffset.UtcNow, "He don't know."),
            new TranscriptEntry(5, DateTimeOffset.UtcNow, "She go to store.")
        };

        var result = DeepDiveInputBuilder.ComputeGrammarDynamics(metrics, transcripts);

        AssertEx.True(result.overall == "good" || result.overall == "poor", "Overall grammar");
        AssertEx.Equal(2, result.distribution["good"], "Good count");
        AssertEx.Equal(2, result.distribution["poor"], "Poor count");
        AssertEx.Equal(1, result.distribution["moderate"], "Moderate count");

        AssertEx.Equal(2, result.notes.Count, "Only poor notes collected");
        AssertEx.Equal("poor", result.notes[0].grammar, "Grammar 1");
        AssertEx.Equal("Me is hungry.", result.notes[0].transcript, "Transcript 1");
        AssertEx.Equal("poor", result.notes[1].grammar, "Grammar 2");
        AssertEx.Equal("She go to store.", result.notes[1].transcript, "Transcript 2");
    }

    private static RetellingHistoryEntry CreateReflection(InquirerConfusionScore score)
    {
        var idea = new GemmaStage.Session.IdeaReflector.IdeaReflectorReflection(
            "topic", "retelling", "main idea understanding");
        return new RetellingHistoryEntry(
            1, DateTimeOffset.UtcNow, "brief",
            Array.Empty<RawContentSegment>(),
            idea,
            new InquirerReflection(Array.Empty<InquirerConcern>(), Array.Empty<RemovedConcern>()),
            new InquirerStateSnapshot("topic", "idea", score, Array.Empty<InquirerConcern>()));
    }
}
