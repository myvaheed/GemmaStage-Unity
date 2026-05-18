using GemmaStage.Session.Inquirer;
using GemmaStage.Session.Prompts;
using System.Text.Json;

namespace GemmaStage.Session.Tests.Inquirer;

internal static class InquirerResponseParserReflectionTests
{
    public static void Run()
    {
        var json = """
            {
              "role": "assistant",
              "content": null,
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_inquirer_reflection",
                    "arguments": "{\"new_concerns\":[{\"id\":4,\"question\":\"How does queued input catch up after a cycle?\",\"type\":\"comprehension_gap\"}],\"removed_concerns\":[{\"id\":0,\"cause\":\"resolved\",\"note\":\"The topic is clear now.\"}]}"
                  }
                }
              ]
            }
            """;

        var parse = InquirerResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"Inquirer reflection parse should succeed: {parse.Error}");
        AssertEx.True(parse.Reflection is not null, "Reflection payload should be populated");

        var reflection = parse.Reflection!;
        AssertEx.Equal(1, reflection.NewConcerns.Count, "New concerns should round-trip");
        AssertEx.Equal(InquirerConcernType.ComprehensionGap, reflection.NewConcerns[0].Type, "Concern type should round-trip");
        AssertEx.True(reflection.NewConcerns[0].TypeReason is null, "TypeReason should be null when omitted from payload");
        AssertEx.Equal(1, reflection.RemovedConcerns.Count, "Removed concerns should round-trip");
        AssertEx.Equal(RemovedConcernCause.Resolved, reflection.RemovedConcerns[0].Cause, "Removal cause should round-trip");
    }
}

internal static class InquirerResponseParserTypeReasonTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_inquirer_reflection",
                    "arguments": "{\"new_concerns\":[{\"id\":2,\"question\":\"Could you give an example of an extraneous element?\",\"type\":\"detail_request\",\"type_reason\":\"Listener understood; wants concrete grounding.\"}],\"removed_concerns\":[]}"
                  }
                }
              ]
            }
            """;

        var parse = InquirerResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"Parse with type_reason should succeed: {parse.Error}");
        AssertEx.Equal(1, parse.Reflection!.NewConcerns.Count, "Reflection should carry one new concern");

        var concern = parse.Reflection!.NewConcerns[0];
        AssertEx.Equal(InquirerConcernType.DetailRequest, concern.Type, "Type should round-trip");
        AssertEx.Equal(
            "Listener understood; wants concrete grounding.",
            concern.TypeReason!,
            "TypeReason should round-trip when present in payload");

        var blankReason = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_inquirer_reflection",
                    "arguments": "{\"new_concerns\":[{\"id\":3,\"question\":\"What is X?\",\"type\":\"comprehension_gap\",\"type_reason\":\"   \"}],\"removed_concerns\":[]}"
                  }
                }
              ]
            }
            """;

        var blankParse = InquirerResponseParser.Parse(blankReason);
        AssertEx.True(!blankParse.IsFailure, "Parse with whitespace type_reason should still succeed");
        AssertEx.True(
            blankParse.Reflection!.NewConcerns[0].TypeReason is null,
            "Whitespace-only type_reason should be normalized to null");
    }
}

internal static class InquirerResponseParserFailureTests
{
    public static void Run()
    {
        var noToolCalls = InquirerResponseParser.Parse("""
            { "role": "assistant", "content": "I prefer prose." }
            """);
        AssertEx.True(noToolCalls.IsFailure, "Missing tool_calls should be a failure");
        AssertEx.Equal("I prefer prose.", noToolCalls.RawAssistantContent!, "Raw content should be surfaced on failure");

        var unknownTool = InquirerResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_idea_reflector_understanding", "arguments": "{}" } }
              ]
            }
            """);
        AssertEx.True(unknownTool.IsFailure, "Unknown tool name should be a failure");

        var missingNewConcerns = InquirerResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_inquirer_reflection",
                    "arguments": "{\"removed_concerns\":[]}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(missingNewConcerns.IsFailure, "new_concerns must be present on every reflection");

        var negativeId = InquirerResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_inquirer_reflection",
                    "arguments": "{\"new_concerns\":[{\"id\":-1,\"question\":\"What is the topic?\",\"type\":\"topic_unknown\"}],\"removed_concerns\":[]}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(negativeId.IsFailure, "Concern ids cannot be negative");

        var badCause = InquirerResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_inquirer_reflection",
                    "arguments": "{\"new_concerns\":[],\"removed_concerns\":[{\"id\":0,\"cause\":\"maybe\",\"note\":\"No clue.\"}]}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(badCause.IsFailure, "Removal cause must be validated");

        var badConcernType = InquirerResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_inquirer_reflection",
                    "arguments": "{\"new_concerns\":[{\"id\":1,\"question\":\"What is X?\",\"type\":\"made_up\"}],\"removed_concerns\":[]}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(badConcernType.IsFailure, "Unknown concern type must be rejected");
    }
}

internal static class InquirerSchemaTests
{
    public static void Run()
    {
        using var doc = JsonDocument.Parse(InquirerPrompts.ToolsJson);
        AssertEx.Equal(JsonValueKind.Array, doc.RootElement.ValueKind, "Inquirer tools_json must be a JSON array");
        AssertEx.Equal(1, doc.RootElement.GetArrayLength(), "Inquirer tools_json must contain one tool definition");

        var function = doc.RootElement[0].GetProperty("function");
        AssertEx.Equal(InquirerSchemas.ReflectionToolName, function.GetProperty("name").GetString()!, "Inquirer tool name should remain stable");

        var parameters = function.GetProperty("parameters");
        var required = parameters.GetProperty("required");
        AssertEx.Equal(2, required.GetArrayLength(), "Required fields after T2: new_concerns, removed_concerns");
        AssertEx.True(!parameters.TryGetProperty("oneOf", out _), "Schema must not gate fields with oneOf anymore");

        var properties = parameters.GetProperty("properties");
        AssertEx.True(!properties.TryGetProperty("topic", out _), "Inquirer schema must no longer expose topic (owned by IdeaReflector)");
        AssertEx.True(!properties.TryGetProperty("main_idea_understanding", out _), "Inquirer schema must no longer expose main_idea_understanding (owned by IdeaReflector)");
        AssertEx.True(!properties.TryGetProperty("confusion_score", out _), "Schema should no longer expose confusion_score (runtime-derived)");
        AssertEx.True(properties.TryGetProperty("new_concerns", out _), "Schema should define new_concerns");
        AssertEx.True(properties.TryGetProperty("removed_concerns", out _), "Schema should define removed_concerns");

        var newConcernItem = properties.GetProperty("new_concerns").GetProperty("items");
        var newConcernRequired = newConcernItem.GetProperty("required");
        AssertEx.Equal(3, newConcernRequired.GetArrayLength(), "new_concerns items require id, question, type");
        var newConcernProperties = newConcernItem.GetProperty("properties");
        var typeEnum = newConcernProperties.GetProperty("type").GetProperty("enum");
        AssertEx.Equal(3, typeEnum.GetArrayLength(), "Concern type enum must list the three supported labels");
        AssertEx.True(
            newConcernProperties.TryGetProperty("type_reason", out _),
            "Schema should expose optional type_reason field for telemetry");
    }
}

internal static class InquirerStateTrackerTests
{
    public static void Run()
    {
        var tracker = new InquirerStateTracker();

        var firstTurn = tracker.PrepareTurn(
            topic: null,
            mainIdeaUnderstanding: null,
            retellings: Array.Empty<string>());
        AssertEx.Equal(1L, firstTurn.ReservedConcernIds[0], "Unknown-topic initialization should reserve ids after the canonical topic concern");
        AssertEx.Equal(1, tracker.OpenConcerns.Count, "Unknown topic should seed the canonical topic concern");
        AssertEx.Equal(InquirerSchemas.TopicConcernQuestion, tracker.OpenConcerns[0].Question, "Canonical topic concern should be inserted");

        var firstReflection = new InquirerReflection(
            new[]
            {
                new InquirerConcern(1, "How does queued input catch up after a cycle?", InquirerConcernType.ComprehensionGap),
                new InquirerConcern(2, "Why keep both Perceptor and Inquirer caches hot?", InquirerConcernType.DetailRequest),
            },
            new[] { new RemovedConcern(0, RemovedConcernCause.Resolved, "The topic is now explicit.") });

        var firstApply = tracker.ApplyReflection(
            firstReflection,
            firstTurn,
            topic: "KV cache budgeting",
            mainIdeaUnderstanding: "Balance cache residency against latency spikes.");

        var snapshot = firstApply.State;
        AssertEx.Equal("KV cache budgeting", snapshot.Topic!, "Topic should be carried into the snapshot from runtime");
        AssertEx.Equal("Balance cache residency against latency spikes.", snapshot.MainIdeaUnderstanding!, "Main idea should be carried into the snapshot from runtime");
        AssertEx.Equal(2, snapshot.Concerns.Count, "Open concerns should keep only the remaining live concerns");
        AssertEx.Equal(1L, snapshot.Concerns[0].Id, "Newest concern should remain at the front");
        AssertEx.Equal(2L, snapshot.Concerns[1].Id, "Second new concern should follow");
        AssertEx.Equal(4L, tracker.NextConcernId, "Reserved ids should advance monotonically after a successful turn");
        AssertEx.Equal(0, firstApply.ArchivedConcerns.Count, "No overflow means no archived concerns");

        var secondTurn = tracker.PrepareTurn(
            topic: "KV cache budgeting",
            mainIdeaUnderstanding: "Balance cache residency against latency spikes.",
            retellings: new[] { "Retelling from cycle 1." });
        AssertEx.Equal(4L, secondTurn.ReservedConcernIds[0], "Reserved ids should continue monotonically on later turns");

        var secondReflection = new InquirerReflection(
            new[]
            {
                new InquirerConcern(5, "How does summarization avoid tail bias?", InquirerConcernType.DetailRequest)
            },
            new[] { new RemovedConcern(1, RemovedConcernCause.Resolved, "The queue-drain answer was provided.") });

        var secondApply = tracker.ApplyReflection(
            secondReflection,
            secondTurn,
            topic: "KV cache budgeting",
            mainIdeaUnderstanding: "Cache residency is balanced against latency by draining queued input quickly after each cycle.");

        var secondSnapshot = secondApply.State;
        AssertEx.Equal(
            "Cache residency is balanced against latency by draining queued input quickly after each cycle.",
            secondSnapshot.MainIdeaUnderstanding!,
            "Main idea should reflect the latest restatement");
        AssertEx.Equal(2, secondSnapshot.Concerns.Count, "Open concerns should reflect removals plus prepended new concerns");
        AssertEx.Equal(5L, secondSnapshot.Concerns[0].Id, "Brand-new concern should be inserted at the front");
        AssertEx.Equal(2L, secondSnapshot.Concerns[1].Id, "Still-open older concern should remain behind new concerns");
        AssertEx.Equal(7L, tracker.NextConcernId, "Reserved ids should keep moving forward across turns");
    }
}

internal static class InquirerUnknownTopicInvariantTests
{
    public static void Run()
    {
        var tracker = new InquirerStateTracker();
        var turn = tracker.PrepareTurn(
            topic: null,
            mainIdeaUnderstanding: null,
            retellings: Array.Empty<string>());

        var reflection = new InquirerReflection(
            Array.Empty<InquirerConcern>(),
            new[] { new RemovedConcern(0, RemovedConcernCause.Irrelevant, "Dropping it would be cleaner.") });

        var apply = tracker.ApplyReflection(
            reflection,
            turn,
            topic: null,
            mainIdeaUnderstanding: null);
        var snapshot = apply.State;
        AssertEx.True(snapshot.Topic is null, "Unknown topic sentinel should remain unmaterialized in persisted state");
        AssertEx.Equal(1, snapshot.Concerns.Count, "Unknown topic invariant should reinsert a topic concern");
        AssertEx.Equal(4L, snapshot.Concerns[0].Id, "Reinserted topic concern should get the next monotonic id after the reserved range");
    }
}

internal static class InquirerConcernOverflowTests
{
    public static void Run()
    {
        var tracker = new InquirerStateTracker(
            openConcerns: new[]
            {
                new InquirerConcern(1, "Newer concern", InquirerConcernType.ComprehensionGap),
                new InquirerConcern(0, "Oldest concern", InquirerConcernType.DetailRequest),
            },
            concernCapacity: 2);

        var turn = tracker.PrepareTurn(
            topic: "KV cache budgeting",
            mainIdeaUnderstanding: "Anchored discussion of cache budgeting.",
            retellings: Array.Empty<string>());
        var reflection = new InquirerReflection(
            new[] { new InquirerConcern(turn.ReservedConcernIds[0], "Newest concern", InquirerConcernType.ComprehensionGap) },
            Array.Empty<RemovedConcern>());

        var apply = tracker.ApplyReflection(
            reflection,
            turn,
            topic: "KV cache budgeting",
            mainIdeaUnderstanding: "Anchored discussion of cache budgeting.");
        AssertEx.Equal(2, apply.State.Concerns.Count, "Capacity should bound the live concern deque");
        AssertEx.Equal("Newest concern", apply.State.Concerns[0].Question, "New concerns should be prepended");
        AssertEx.Equal("Oldest concern", apply.ArchivedConcerns[0].Question, "Overflow should archive the oldest concern from the back");
    }
}

internal static class InquirerArchiveOpenConcernsTests
{
    public static void Run()
    {
        var tracker = new InquirerStateTracker(
            openConcerns: new[]
            {
                new InquirerConcern(4, "Newest remaining concern", InquirerConcernType.ComprehensionGap),
                new InquirerConcern(2, "Older remaining concern", InquirerConcernType.DetailRequest),
            });

        var archived = tracker.ArchiveOpenConcerns();
        var snapshot = tracker.Snapshot(topic: null, mainIdeaUnderstanding: null);

        AssertEx.Equal(2, archived.Count, "Archiving at session end should return all remaining live concerns");
        AssertEx.Equal(4L, archived[0].Id, "Archive should preserve live concern order");
        AssertEx.Equal(0, snapshot.Concerns.Count, "No live concerns should remain after session-end archiving");
    }
}

internal static class InquirerDeriveConfusionScoreTests
{
    public static void Run()
    {
        AssertEx.Equal(
            InquirerConfusionScore.Low,
            InquirerStateTracker.DeriveConfusionScore(Array.Empty<InquirerConcern>()),
            "Empty concern set should map to Low");

        AssertEx.Equal(
            InquirerConfusionScore.Low,
            InquirerStateTracker.DeriveConfusionScore(new[]
            {
                new InquirerConcern(1, "What about ABI?", InquirerConcernType.DetailRequest),
                new InquirerConcern(2, "Why now?", InquirerConcernType.DetailRequest),
            }),
            "Detail-only concerns reflect engagement, not confusion");

        AssertEx.Equal(
            InquirerConfusionScore.Medium,
            InquirerStateTracker.DeriveConfusionScore(new[]
            {
                new InquirerConcern(1, "What does X mean?", InquirerConcernType.ComprehensionGap),
                new InquirerConcern(2, "Show me an example.", InquirerConcernType.DetailRequest),
            }),
            "One comprehension gap should map to Medium");

        AssertEx.Equal(
            InquirerConfusionScore.High,
            InquirerStateTracker.DeriveConfusionScore(new[]
            {
                new InquirerConcern(1, "What is X?", InquirerConcernType.ComprehensionGap),
                new InquirerConcern(2, "What is Y?", InquirerConcernType.ComprehensionGap),
                new InquirerConcern(3, "What is Z?", InquirerConcernType.ComprehensionGap),
            }),
            "Three comprehension gaps should map to High");

        AssertEx.Equal(
            InquirerConfusionScore.VeryHigh,
            InquirerStateTracker.DeriveConfusionScore(new[]
            {
                new InquirerConcern(0, InquirerSchemas.TopicConcernQuestion, InquirerConcernType.TopicUnknown),
                new InquirerConcern(1, "Show an example", InquirerConcernType.DetailRequest),
            }),
            "Any topic_unknown concern dominates and forces VeryHigh");
    }
}

internal static class InquirerLoadConcernsForFinalQATests
{
    public static void Run()
    {
        var tracker = new InquirerStateTracker(
            openConcerns: new[]
            {
                new InquirerConcern(2, "How does X work?", InquirerConcernType.ComprehensionGap),
                new InquirerConcern(4, "Why prioritize Y?", InquirerConcernType.DetailRequest),
            });

        var archived = tracker.ArchiveOpenConcerns();
        AssertEx.Equal(0, tracker.OpenConcerns.Count, "Open concerns should be empty after archiving");

        tracker.LoadOpenConcerns(new[]
        {
            new InquirerConcern(2, "How does X work?", InquirerConcernType.ComprehensionGap),
        });

        var snapshot = tracker.Snapshot(topic: "X", mainIdeaUnderstanding: "About X");
        AssertEx.Equal(1, snapshot.Concerns.Count, "LoadOpenConcerns should replace the live concern set");
        AssertEx.Equal(2L, snapshot.Concerns[0].Id, "Loaded concern id should round-trip");
        AssertEx.Equal("How does X work?", snapshot.Concerns[0].Question, "Loaded concern question should round-trip");
        AssertEx.Equal(InquirerConcernType.ComprehensionGap, snapshot.Concerns[0].Type, "Loaded concern type should round-trip");
        AssertEx.True(tracker.NextConcernId >= 3, "NextConcernId should advance past the max loaded id");
        AssertEx.Equal(InquirerConfusionScore.Medium, tracker.ConfusionScore!.Value, "ConfusionScore should reflect the loaded concern set");
    }
}

internal static class InquirerPromptStructureTests
{
    public static void Run()
    {
        var tracker = new InquirerStateTracker();

        // First cycle — no retellings yet.
        var firstTurn = tracker.PrepareTurn(
            topic: "Sample",
            mainIdeaUnderstanding: "Anchor",
            retellings: Array.Empty<string>());

        AssertEx.True(
            firstTurn.Prompt.Contains("(none — this is the first cycle)"),
            "First cycle prompt should indicate no retellings yet");
        AssertEx.True(
            firstTurn.Prompt.Contains("Current topic: Sample"),
            "Inquirer prompt should surface topic");
        AssertEx.True(
            firstTurn.Prompt.Contains("Current main idea understanding: Anchor"),
            "Inquirer prompt should surface main idea understanding");

        // Second cycle — multiple retellings already accumulated.
        var secondTurn = tracker.PrepareTurn(
            topic: "Sample",
            mainIdeaUnderstanding: "Anchor with more depth.",
            retellings: new[] { "Retelling A.", "Retelling B." });

        AssertEx.True(
            secondTurn.Prompt.Contains("Retellings (all cycles so far, most recent last):"),
            "Inquirer prompt should advertise the full retellings history (all cycles, not last N)");
        AssertEx.True(
            secondTurn.Prompt.Contains("[#1] Retelling A."),
            "First retelling must carry [#1] label");
        AssertEx.True(
            secondTurn.Prompt.Contains("[#2] Retelling B."),
            "Second retelling must carry [#2] label");
        AssertEx.True(
            !secondTurn.Prompt.Contains("(none — this is the first cycle)"),
            "Non-first cycle should not show the 'first cycle' notice");
    }

    public static void RunRestrictedMode()
    {
        var tracker = new InquirerStateTracker();

        var unrestricted = tracker.PrepareTurn(
            topic: "Sample",
            mainIdeaUnderstanding: "Anchor",
            retellings: new[] { "Retelling A." });
        AssertEx.True(
            unrestricted.Prompt.Contains("Emit only brand-new concerns in new_concerns"),
            "Unrestricted prompt must keep the standard new-concerns instruction");
        AssertEx.True(
            !unrestricted.Prompt.Contains("Final Q&A mode"),
            "Unrestricted prompt must not mention Final Q&A mode");

        var restricted = tracker.PrepareTurn(
            topic: "Sample",
            mainIdeaUnderstanding: "Anchor",
            retellings: new[] { "Retelling A.", "Retelling B." },
            restricted: true);

        AssertEx.True(
            restricted.Prompt.Contains("Final Q&A mode"),
            "Restricted prompt must call out Final Q&A mode explicitly");
        AssertEx.True(
            restricted.Prompt.Contains("leave new_concerns as an empty array"),
            "Restricted prompt must instruct the model to leave new_concerns empty");
        AssertEx.True(
            restricted.Prompt.Contains("Removals (cause=resolved or cause=irrelevant) remain allowed"),
            "Restricted prompt must keep the removal channel open");
        AssertEx.True(
            !restricted.Prompt.Contains("Use only reserved ids for brand-new concerns"),
            "Restricted prompt must drop the brand-new-concern guidance entirely");
    }
}
