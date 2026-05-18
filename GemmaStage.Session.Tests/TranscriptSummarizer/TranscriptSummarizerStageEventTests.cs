using GemmaStage.Session.TranscriptSummarizer;

namespace GemmaStage.Session.Tests.TranscriptSummarizer;

internal static class TranscriptSummarizerStageEventTests
{
    public static void Run()
    {
        var events = new GemmaStage.Session.SessionEvents();
        TranscriptSummarizerMapStageResult? mapSeen = null;
        TranscriptSummarizerReduceStageResult? reduceSeen = null;

        events.TranscriptSummarizerMapCompleted += result => mapSeen = result;
        events.TranscriptSummarizerReduceCompleted += result => reduceSeen = result;

        var mapOutputs = new[]
        {
            new TranscriptSummarizerMapOutput(
                "The speaker introduced the topic.",
                "intro",
                "consistent",
                "moderate",
                Array.Empty<string>()),
        };

        events.FireTranscriptSummarizerMapCompleted(
            new TranscriptSummarizerMapStageResult(
                mapOutputs,
                Array.Empty<TranscriptSummarizerTurnResult>()));

        var reduceOutput = new TranscriptSummarizerReduceOutput(
            InferredMainIdeaFromTranscript: "Introduce the topic clearly.");

        events.FireTranscriptSummarizerReduceCompleted(
            new TranscriptSummarizerReduceStageResult(
                mapOutputs,
                reduceOutput,
                null));

        AssertEx.True(mapSeen is not null, "TranscriptSummarizerMapCompleted should fire");
        AssertEx.Equal(1, mapSeen!.MapOutputs.Count, "MAP stage payload should round-trip outputs");
        AssertEx.Equal("intro", mapSeen.MapOutputs[0].Structure, "MAP stage payload should preserve structure");

        AssertEx.True(reduceSeen is not null, "TranscriptSummarizerReduceCompleted should fire");
        AssertEx.Equal(1, reduceSeen!.MapOutputs.Count, "REDUCE stage payload should keep MAP context");
        AssertEx.True(reduceSeen.ReduceOutput is not null, "REDUCE stage payload should carry the reduce output");
        AssertEx.Equal("Introduce the topic clearly.", reduceSeen.ReduceOutput!.InferredMainIdeaFromTranscript,
            "REDUCE stage payload should preserve inferred main idea");
    }
}
