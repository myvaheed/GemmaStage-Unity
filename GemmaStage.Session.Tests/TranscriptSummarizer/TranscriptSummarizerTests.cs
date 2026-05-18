using GemmaStage.Session.Prompts;
using GemmaStage.Session.TranscriptSummarizer;
using System.Text.Json;

namespace GemmaStage.Session.Tests.TranscriptSummarizer;

internal static class TranscriptSummarizerResponseParserMapRetellingTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_chunk_retelling",
                    "arguments": "{\"retelling\":\"The speaker introduced the concept of neural networks.\"}"
                  }
                }
              ]
            }
            """;

        var parse = TranscriptSummarizerResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"MAP-retelling parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsMapRetelling, "Should be a map-retelling result");
        AssertEx.True(!parse.IsMapSignals, "Should not be a map-signals result");
        AssertEx.True(!parse.IsReduce, "Should not be a reduce result");

        var retelling = parse.MapRetelling!;
        AssertEx.True(retelling.Retelling.Contains("neural networks"), "Retelling should round-trip");
    }
}

internal static class TranscriptSummarizerResponseParserMapSignalsTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_chunk_signals",
                    "arguments": "{\"structure\":\"intro\",\"consistency\":\"consistent\",\"support\":\"moderate\",\"notes\":[\"Speaker used a whiteboard diagram.\"]}"
                  }
                }
              ]
            }
            """;

        var parse = TranscriptSummarizerResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"MAP-signals parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsMapSignals, "Should be a map-signals result");
        AssertEx.True(!parse.IsMapRetelling, "Should not be a map-retelling result");

        var signals = parse.MapSignals!;
        AssertEx.Equal("intro", signals.Structure, "Structure should round-trip");
        AssertEx.Equal("consistent", signals.Consistency, "Consistency should round-trip");
        AssertEx.Equal("moderate", signals.Support, "Support should round-trip");
        AssertEx.Equal(1, signals.Notes.Count, "Notes should contain one entry");
        AssertEx.True(signals.Notes[0].Contains("whiteboard"), "Note should round-trip");
    }
}

internal static class TranscriptSummarizerResponseParserMapSignalsNoNotesTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_chunk_signals",
                    "arguments": "{\"structure\":\"development\",\"consistency\":\"minor_drift\",\"support\":\"weak\"}"
                  }
                }
              ]
            }
            """;

        var parse = TranscriptSummarizerResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"MAP-signals parse without notes should succeed: {parse.Error}");
        AssertEx.True(parse.IsMapSignals, "Should be a map-signals result");

        var signals = parse.MapSignals!;
        AssertEx.Equal(0, signals.Notes.Count, "Notes should be empty when omitted");
        AssertEx.Equal("development", signals.Structure, "Structure should round-trip");
        AssertEx.Equal("minor_drift", signals.Consistency, "Consistency should round-trip");
    }
}

internal static class TranscriptSummarizerResponseParserReduceTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_transcript_summary",
                    "arguments": "{\"inferred_main_idea_from_transcript\":\"Machine learning fundamentals for beginners.\"}"
                  }
                }
              ]
            }
            """;

        var parse = TranscriptSummarizerResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"REDUCE parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsReduce, "Should be a reduce result");
        AssertEx.True(!parse.IsMapRetelling, "Should not be a map-retelling result");
        AssertEx.True(!parse.IsMapSignals, "Should not be a map-signals result");

        var reduce = parse.Reduce!;
        AssertEx.True(reduce.InferredMainIdeaFromTranscript.Contains("Machine learning"),
            "Inferred main idea should round-trip");
    }
}

internal static class TranscriptSummarizerResponseParserReduceEmptyMainIdeaTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_transcript_summary",
                    "arguments": "{\"inferred_main_idea_from_transcript\":\"   \"}"
                  }
                }
              ]
            }
            """;

        var parse = TranscriptSummarizerResponseParser.Parse(json);
        AssertEx.True(parse.IsFailure, "Whitespace-only inferred_main_idea_from_transcript should be a failure");
    }
}

internal static class TranscriptSummarizerResponseParserFailureTests
{
    public static void Run()
    {
        var noToolCalls = TranscriptSummarizerResponseParser.Parse("""
            { "role": "assistant", "content": "I prefer prose." }
            """);
        AssertEx.True(noToolCalls.IsFailure, "Missing tool_calls should be a failure");

        var unknownTool = TranscriptSummarizerResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_something_else", "arguments": "{}" } }
              ]
            }
            """);
        AssertEx.True(unknownTool.IsFailure, "Unknown tool name should be a failure");

        var emptyRetelling = TranscriptSummarizerResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_chunk_retelling", "arguments": "{\"retelling\":\"   \"}" } }
              ]
            }
            """);
        AssertEx.True(emptyRetelling.IsFailure, "Whitespace-only retelling should be a failure");

        var emptyPayload = TranscriptSummarizerResponseParser.Parse("");
        AssertEx.True(emptyPayload.IsFailure, "Empty payload should be a failure");
    }
}

internal static class TranscriptSummarizerSchemaTests
{
    public static void Run()
    {
        // MAP-retelling: single string field.
        using var retellingDoc = JsonDocument.Parse(TranscriptSummarizerMapRetellingPrompts.ToolsJson);
        AssertEx.Equal(1, retellingDoc.RootElement.GetArrayLength(), "MAP-retelling tools_json must contain one tool");
        var retellingTool = retellingDoc.RootElement[0].GetProperty("function");
        AssertEx.Equal(TranscriptSummarizerSchemas.MapRetellingToolName, retellingTool.GetProperty("name").GetString()!,
            "MAP-retelling tool should be report_chunk_retelling");
        var retellingProps = retellingTool.GetProperty("parameters").GetProperty("properties");
        AssertEx.True(retellingProps.TryGetProperty("retelling", out _), "MAP-retelling schema must expose retelling");
        AssertEx.True(!retellingProps.TryGetProperty("structure", out _),
            "MAP-retelling schema must NOT expose tag fields (signals call owns those)");

        // MAP-signals: enum tags + optional notes; no retelling.
        using var signalsDoc = JsonDocument.Parse(TranscriptSummarizerMapSignalsPrompts.ToolsJson);
        AssertEx.Equal(1, signalsDoc.RootElement.GetArrayLength(), "MAP-signals tools_json must contain one tool");
        var signalsTool = signalsDoc.RootElement[0].GetProperty("function");
        AssertEx.Equal(TranscriptSummarizerSchemas.MapSignalsToolName, signalsTool.GetProperty("name").GetString()!,
            "MAP-signals tool should be report_chunk_signals");
        var signalsProps = signalsTool.GetProperty("parameters").GetProperty("properties");
        AssertEx.True(!signalsProps.TryGetProperty("retelling", out _),
            "MAP-signals schema must NOT expose retelling (retelling call owns it)");
        AssertEx.True(signalsProps.TryGetProperty("structure", out _), "MAP-signals schema must expose structure");
        AssertEx.True(signalsProps.TryGetProperty("consistency", out _), "MAP-signals schema must expose consistency");
        AssertEx.True(signalsProps.TryGetProperty("support", out _), "MAP-signals schema must expose support");

        // REDUCE: single field — only the inferred main idea synthesised from
        // the chunk retellings. Per-chunk signals (structure / consistency /
        // support) are now consumed directly by the per-criterion DeepDive
        // sub-roles, so REDUCE no longer aggregates them.
        using var reduceDoc = JsonDocument.Parse(TranscriptSummarizerReducePrompts.ToolsJson);
        AssertEx.Equal(1, reduceDoc.RootElement.GetArrayLength(), "REDUCE tools_json must contain one tool definition");
        var reduceTool = reduceDoc.RootElement[0].GetProperty("function");
        AssertEx.Equal(TranscriptSummarizerSchemas.ReduceToolName, reduceTool.GetProperty("name").GetString()!,
            "REDUCE tool should be report_transcript_summary");

        var reduceProps = reduceTool.GetProperty("parameters").GetProperty("properties");
        AssertEx.True(reduceProps.TryGetProperty("inferred_main_idea_from_transcript", out _),
            "REDUCE schema must expose inferred_main_idea_from_transcript");
        AssertEx.True(!reduceProps.TryGetProperty("structure_analysis", out _),
            "REDUCE schema must not expose structure_analysis");
        AssertEx.True(!reduceProps.TryGetProperty("consistency_analysis", out _),
            "REDUCE schema must not expose consistency_analysis");
        AssertEx.True(!reduceProps.TryGetProperty("support_analysis", out _),
            "REDUCE schema must not expose support_analysis");
        AssertEx.True(!reduceProps.TryGetProperty("consistency_issues", out _),
            "REDUCE schema must not expose consistency_issues");
        AssertEx.True(!reduceProps.TryGetProperty("global_summary", out _),
            "REDUCE schema must not expose global_summary");
        AssertEx.True(!reduceProps.TryGetProperty("structure_pattern", out _),
            "REDUCE schema must not expose structure_pattern");
    }
}
