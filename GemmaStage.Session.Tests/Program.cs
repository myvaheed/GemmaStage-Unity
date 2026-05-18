using GemmaStage.Session;
using GemmaStage.Session.Stores;
using GemmaStage.Session.Tests.Audio;
using GemmaStage.Session.Tests.Clarification;
using GemmaStage.Session.Tests.CognitiveCycle;
using GemmaStage.Session.Tests.GroundTruthSummarizer;
using GemmaStage.Session.Tests.IdeaReflector;
using GemmaStage.Session.Tests.Inquirer;
using GemmaStage.Session.Tests.LiveQA;
using GemmaStage.Session.Tests.MainIdeaComparator;
using GemmaStage.Session.Tests.Perceptor;
using GemmaStage.Session.Tests.Stores;
using GemmaStage.Session.Tests.TranscriptSummarizer;

var runner = new TestRunner();
runner.Run("SessionConfig defaults to 8K context", SessionConfigTests.RunDefaults);
runner.Run("TranscriptStore append + snapshot", TranscriptStoreTests.Run);
runner.Run("ImageStore append + snapshot", ImageStoreTests.Run);
runner.Run("MetricsStore append + snapshot", MetricsStoreTests.Run);
runner.Run("ConcernStore append + snapshot", ConcernStoreTests.Run);
runner.Run("ConcernArchive append + snapshot", ConcernArchiveTests.Run);
runner.Run("InputQueue append + snapshot", InputQueueTests.Run);
runner.Run("RawContentBuffer preserves chronological audio + image segments and drains atomically", RawContentBufferTests.Run);
runner.Run("RawContentBuffer interleaves Q&A markers between audio segments", RawContentBufferTests.RunMarkers);
runner.Run("QaRoundsHistory records closed Live and Final rounds with concern id", QaRoundsHistoryTests.Run);
runner.Run("AsrResponseParser parses audio tool call", AsrResponseParserAudioTests.Run);
runner.Run("AsrResponseParser parses poor-clarity audio", AsrResponseParserPoorClarityTests.Run);
runner.Run("AsrResponseParser surfaces failures (incl. non-audio tools)", AsrResponseParserFailureTests.Run);
runner.Run("AsrSchemas tools shape (audio only with renamed field)", AsrSchemaTests.Run);
runner.Run("AsrConversation derives chunk_completed from transcript punctuation", AsrChunkCompletedDerivationTests.Run);
runner.Run("I2tResponseParser parses image tool call", I2tResponseParserImageTests.Run);
runner.Run("I2tResponseParser rejects non-image tools", I2tResponseParserOtherToolRejectedTests.Run);
runner.Run("I2tSchemas tools shape (image only)", I2tSchemaTests.Run);
runner.Run("IdeaReflector placeholder (per-role tests pending refactor)", IdeaReflectorPlaceholderTests.Run);
runner.Run("InquirerResponseParser parses concerns-only reflection", InquirerResponseParserReflectionTests.Run);
runner.Run("InquirerResponseParser parses optional type_reason field", InquirerResponseParserTypeReasonTests.Run);
runner.Run("InquirerResponseParser surfaces failures", InquirerResponseParserFailureTests.Run);
runner.Run("InquirerSchemas tools shape (concerns only)", InquirerSchemaTests.Run);
runner.Run("InquirerStateTracker keeps concern ids monotonic", InquirerStateTrackerTests.Run);
runner.Run("InquirerStateTracker preserves unknown-topic invariant", InquirerUnknownTopicInvariantTests.Run);
runner.Run("InquirerStateTracker archives overflowed concerns", InquirerConcernOverflowTests.Run);
runner.Run("InquirerStateTracker archives all open concerns at session end", InquirerArchiveOpenConcernsTests.Run);
runner.Run("InquirerStateTracker loads clarification concerns for Final Q&A", InquirerLoadConcernsForFinalQATests.Run);
runner.Run("Inquirer derives confusion score from concern types", InquirerDeriveConfusionScoreTests.Run);
runner.Run("Inquirer prompt renders retellings history and read-only state", InquirerPromptStructureTests.Run);
runner.Run("Inquirer restricted-mode prompt blocks new_concerns and keeps removals open", InquirerPromptStructureTests.RunRestrictedMode);
runner.Run("CycleScheduler gates on elapsed time + chunk_completed", CycleSchedulerTests.Run);
runner.Run("CycleScheduler can gate on audio input duration", CycleSchedulerTests.RunAudioTimeline);
runner.Run("SessionConfig.TranscriptSummarizerTokenBudget default is 500", SessionConfigTranscriptSummarizerDefaultTests.Run);

runner.Run("WavReader reads standard mono wav", WavReaderTests.Run);
runner.Run("VolumeClassifier identifies volume chunks", VolumeClassifierTests.Run);
runner.Run("AudioChunker emits expected chunks", AudioChunkerTests.Run);

runner.Run("ClarificationResponseParser parses revise tool call", ClarificationResponseParserReviseTests.Run);
runner.Run("ClarificationResponseParser parses resolve tool call", ClarificationResponseParserResolveTests.Run);
runner.Run("ClarificationResponseParser parses empty resolve", ClarificationResponseParserEmptyResolveTests.Run);
runner.Run("ClarificationResponseParser surfaces failures", ClarificationResponseParserFailureTests.Run);
runner.Run("ClarificationSchemas tools shape", ClarificationSchemaTests.Run);
runner.Run("TranscriptChunkBuilder boundary logic", TranscriptChunkBuilderTests.Run);

runner.Run("TranscriptSummarizerResponseParser parses MAP-retelling tool call", TranscriptSummarizerResponseParserMapRetellingTests.Run);
runner.Run("TranscriptSummarizerResponseParser parses MAP-signals tool call", TranscriptSummarizerResponseParserMapSignalsTests.Run);
runner.Run("TranscriptSummarizerResponseParser parses MAP-signals without notes", TranscriptSummarizerResponseParserMapSignalsNoNotesTests.Run);
runner.Run("TranscriptSummarizerResponseParser parses REDUCE tool call", TranscriptSummarizerResponseParserReduceTests.Run);
runner.Run("TranscriptSummarizerResponseParser rejects empty inferred main idea", TranscriptSummarizerResponseParserReduceEmptyMainIdeaTests.Run);
runner.Run("TranscriptSummarizerResponseParser surfaces failures", TranscriptSummarizerResponseParserFailureTests.Run);
runner.Run("TranscriptSummarizerSchemas tools shape (retelling + signals + reduce)", TranscriptSummarizerSchemaTests.Run);
runner.Run("TranscriptSummarizer stage events round-trip payloads", TranscriptSummarizerStageEventTests.Run);
runner.Run("GroundTruth MAP parser round-trips claims", GroundTruthMapParserTests.Run);
runner.Run("GroundTruth REDUCE parser round-trips main_thesis + claims", GroundTruthReduceParserTests.Run);
runner.Run("GroundTruth schemas (MAP + REDUCE tool names)", GroundTruthSchemaTests.Run);
runner.Run("GroundTruth chunker emits chunks per token budget", GroundTruthChunkBuildingTests.Run);
runner.Run("GroundTruthSourceKindDetector classifies extensions", GroundTruthSourceKindDetectorTests.Run);
runner.Run("MainIdeaComparatorResponseParser parses coverage tool call (yes/partial/no, optional evidence)", MainIdeaComparatorResponseParserCoverageTests.Run);
runner.Run("MainIdeaComparatorResponseParser parses thesis comparison tool call", MainIdeaComparatorResponseParserThesisTests.Run);
runner.Run("MainIdeaComparatorResponseParser surfaces failures", MainIdeaComparatorResponseParserFailureTests.Run);
runner.Run("MainIdeaComparatorSchemas tools shape (coverage + drift; decompose removed)", MainIdeaComparatorSchemaTests.Run);
runner.Run("MainIdeaComparator Recall scoring (yes=1, partial=0.5, no=0; gt_only contributes 0)", MainIdeaComparatorRecallScoringTests.Run);
runner.Run("MainIdeaComparator coverage + drift prompt shapes (1:1 inputs + audience-only/gt-only blocks)", MainIdeaComparatorPromptShapeTests.Run);
runner.Run("MainIdeaComparator skipped result contract", MainIdeaComparatorRunnerSkipTests.Run);
runner.Run("DeepDiveInputBuilder helper methods (confusion, emotion, grammar, language quality)", GemmaStage.Session.Tests.DeepDive.DeepDiveInputBuilderTests.Run);
runner.Run("DeepDive sub-role parser accepts and validates a single criterion tool call", GemmaStage.Session.Tests.DeepDive.DeepDiveSubRoleParserTests.Run);
runner.Run("DeepDive coordinator skip rules for empty Emotional and zero Q&A", GemmaStage.Session.Tests.DeepDive.DeepDiveCoordinatorSkipTests.Run);
runner.Run("DeepDive sub-role prompts reference their own sections only and define one tool each", GemmaStage.Session.Tests.DeepDive.DeepDiveSubRolePromptTests.Run);

runner.Run("LiveQAConcernPicker random pick covers the open set", LiveQAConcernPickerTests.Run);
runner.Run("OpenConcernsEvent payload round-trips index and concerns", OpenConcernsEventTests.Run);
runner.Run("LiveQARound.ClosingMarkerText matches the canonical sentinel", LiveQARoundConstantsTests.Run);
runner.PrintSummary();

return runner.ExitCode;

internal sealed class TestRunner
{
    private int _passed;
    private int _failed;

    public int ExitCode => _failed == 0 ? 0 : 1;

    public void Run(string name, Action test)
    {
        Console.WriteLine($"[TEST] {name}");

        try
        {
            test();
            _passed++;
            Console.WriteLine("  PASS");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL: {ex.Message}");
        }

        Console.WriteLine();
    }

    public void PrintSummary()
    {
        Console.WriteLine($"Results: {_passed} passed, {_failed} failed");
    }
}

internal static class AssertEx
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string message)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}. Expected={expected} Actual={actual}");
        }
    }
}

internal static class SessionConfigTests
{
    public static void RunDefaults()
    {
        var config = new SessionConfig("model.gguf");
        AssertEx.Equal(8192, config.ContextSize, "Engine-wide context default should be 8K (main-slot roles run sequentially)");
        AssertEx.Equal("English", config.Language, "Language should default to English");
        AssertEx.True(!config.EnableLiveQA, "Live Q&A should default off");
        AssertEx.True(config.TimeLimit is null, "Time limit should be optional by default");
    }
}

internal static class TranscriptStoreTests
{
    public static void Run()
    {
        var store = new TranscriptStore();
        store.Append(new TranscriptEntry(1, DateTimeOffset.Parse("2026-04-21T10:00:00+00:00"), "hello world"));

        var snapshot = store.Snapshot();
        AssertEx.Equal(1, snapshot.Count, "Transcript snapshot count should match append count");
        AssertEx.Equal("hello world", snapshot[0].Text, "Transcript text should round-trip");
    }
}

internal static class ImageStoreTests
{
    public static void Run()
    {
        var store = new ImageStore();
        store.Append(new ImageEntry(2, DateTimeOffset.Parse("2026-04-21T10:01:00+00:00"), "slide with architecture diagram"));

        var snapshot = store.Snapshot();
        AssertEx.Equal(1, snapshot.Count, "Image snapshot count should match append count");
        AssertEx.Equal("slide with architecture diagram", snapshot[0].Examination, "Image examination should round-trip");
    }
}

internal static class MetricsStoreTests
{
    public static void Run()
    {
        var store = new MetricsStore();
        store.Append(new MetricEntry(3, DateTimeOffset.Parse("2026-04-21T10:02:00+00:00"), "normal", "calm", "excellent", true, 0.42, 118.5, 24));

        var snapshot = store.Snapshot();
        AssertEx.Equal(1, snapshot.Count, "Metrics snapshot count should match append count");
        AssertEx.Equal("normal", snapshot[0].Clarity!, "Clarity should round-trip");
        AssertEx.True(snapshot[0].ChunkCompleted, "ChunkCompleted should round-trip");
        AssertEx.Equal(24, snapshot[0].DecodeTokenCount!.Value, "Decode token count should round-trip");
    }
}

internal static class ConcernStoreTests
{
    public static void Run()
    {
        var store = new ConcernStore();
        var updatedAt = DateTimeOffset.Parse("2026-04-21T10:03:00+00:00");

        store.ReplaceOpen(
            new[]
            {
                new GemmaStage.Session.Inquirer.InquirerConcern(8, "Why is P/Invoke needed?", GemmaStage.Session.Inquirer.InquirerConcernType.ComprehensionGap),
                new GemmaStage.Session.Inquirer.InquirerConcern(7, "What problem is being solved?", GemmaStage.Session.Inquirer.InquirerConcernType.DetailRequest),
            },
            updatedAt);

        var snapshot = store.Snapshot();
        var openSnapshot = store.OpenSnapshot();

        AssertEx.Equal(2, snapshot.Count, "Concern snapshot should include the current live concerns");
        AssertEx.Equal(2, openSnapshot.Count, "Open snapshot should mirror the current live concerns");
        AssertEx.Equal(8L, openSnapshot[0].Id, "Open concerns should preserve runtime order");
    }
}

internal static class ConcernArchiveTests
{
    public static void Run()
    {
        var archive = new ConcernArchive();
        var archivedAt = DateTimeOffset.Parse("2026-04-21T10:04:00+00:00");
        archive.AppendOverflow(
            new[]
            {
                new ArchivedConcernEntry(2, "Old concern", GemmaStage.Session.Inquirer.InquirerConcernType.ComprehensionGap, archivedAt, ConcernArchive.OverflowReason),
                new ArchivedConcernEntry(3, "Older concern", GemmaStage.Session.Inquirer.InquirerConcernType.DetailRequest, archivedAt.AddSeconds(1), ConcernArchive.OverflowReason),
            });
        archive.AppendSessionEnd(
            new[]
            {
                new ArchivedConcernEntry(7, "Remaining open concern", GemmaStage.Session.Inquirer.InquirerConcernType.TopicUnknown, archivedAt.AddSeconds(2), ConcernArchive.SessionEndReason),
            });

        var snapshot = archive.Snapshot();
        AssertEx.Equal(3, snapshot.Count, "Archive snapshot should include overflow and session-end concerns");
        AssertEx.Equal(ConcernArchive.OverflowReason, snapshot[0].Reason, "Overflow reason should round-trip");
        AssertEx.Equal(ConcernArchive.SessionEndReason, snapshot[2].Reason, "Session-end reason should round-trip");
    }
}

internal static class InputQueueTests
{
    public static void Run()
    {
        var queue = new InputQueue();
        var audio = new byte[] { 1, 2, 3 };
        var image = new byte[] { 9, 8 };

        queue.EnqueueAudio(audio);
        queue.EnqueueImage(image);

        audio[0] = 99;
        image[0] = 77;

        var snapshot = queue.Snapshot();
        AssertEx.Equal(2, snapshot.Count, "Queue snapshot count should match enqueued items");
        AssertEx.Equal(InputKind.Audio, snapshot[0].Kind, "Audio entry should stay first");
        AssertEx.Equal(InputKind.Image, snapshot[1].Kind, "Image entry should stay second");
        AssertEx.Equal((byte)1, snapshot[0].Payload.Span[0], "Queue should store a defensive copy of audio");
        AssertEx.Equal((byte)9, snapshot[1].Payload.Span[0], "Queue should store a defensive copy of image");

        AssertEx.True(queue.TryDequeue(out var first), "First dequeue should succeed");
        AssertEx.True(first is not null, "First dequeue should return an entry");
        AssertEx.Equal(InputKind.Audio, first!.Kind, "Dequeue order should match enqueue order");
    }
}
