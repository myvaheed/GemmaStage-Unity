using GemmaStage.Session.Clarification;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Transcript;
using System.Text.Json;

namespace GemmaStage.Session.Tests.Clarification;

internal static class ClarificationResponseParserReviseTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_revised_questions",
                    "arguments": "{\"questions\":[{\"id\":1,\"question\":\"Why is sleep important?\",\"type\":\"comprehension_gap\"},{\"id\":3,\"question\":\"How does sleep affect memory?\",\"type\":\"detail_request\"}]}"
                  }
                }
              ]
            }
            """;

        var parse = ClarificationResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"Revise parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsRevise, "Should be a revise result");
        AssertEx.True(!parse.IsResolve, "Should not be a resolve result");

        var revise = parse.Revise!;
        AssertEx.Equal(2, revise.Questions.Count, "Should return two revised questions");
        AssertEx.Equal(1L, revise.Questions[0].Id, "First question id should round-trip");
        AssertEx.Equal("Why is sleep important?", revise.Questions[0].Question, "First question text should round-trip");
        AssertEx.Equal(InquirerConcernType.ComprehensionGap, revise.Questions[0].Type, "First question type should round-trip");
        AssertEx.Equal(3L, revise.Questions[1].Id, "Second question id should round-trip");
        AssertEx.Equal(InquirerConcernType.DetailRequest, revise.Questions[1].Type, "Second question type should round-trip");
    }
}

internal static class ClarificationResponseParserResolveTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_resolved_concerns",
                    "arguments": "{\"resolved_ids\":[2,5]}"
                  }
                }
              ]
            }
            """;

        var parse = ClarificationResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"Resolve parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsResolve, "Should be a resolve result");
        AssertEx.True(!parse.IsRevise, "Should not be a revise result");

        var resolve = parse.Resolve!;
        AssertEx.Equal(2, resolve.ResolvedIds.Count, "Should return two resolved ids");
        AssertEx.Equal(2L, resolve.ResolvedIds[0], "First resolved id should round-trip");
        AssertEx.Equal(5L, resolve.ResolvedIds[1], "Second resolved id should round-trip");
    }
}

internal static class ClarificationResponseParserEmptyResolveTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_resolved_concerns",
                    "arguments": "{\"resolved_ids\":[]}"
                  }
                }
              ]
            }
            """;

        var parse = ClarificationResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"Empty resolve should succeed: {parse.Error}");
        AssertEx.Equal(0, parse.Resolve!.ResolvedIds.Count, "Empty resolved_ids should round-trip");
    }
}

internal static class ClarificationResponseParserFailureTests
{
    public static void Run()
    {
        var noToolCalls = ClarificationResponseParser.Parse("""
            { "role": "assistant", "content": "I prefer prose." }
            """);
        AssertEx.True(noToolCalls.IsFailure, "Missing tool_calls should be a failure");

        var unknownTool = ClarificationResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_something_else", "arguments": "{}" } }
              ]
            }
            """);
        AssertEx.True(unknownTool.IsFailure, "Unknown tool name should be a failure");

        var emptyPayload = ClarificationResponseParser.Parse("");
        AssertEx.True(emptyPayload.IsFailure, "Empty payload should be a failure");

        // Unknown enum value on the type field should fail with a schema error.
        var badType = ClarificationResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_revised_questions",
                    "arguments": "{\"questions\":[{\"id\":1,\"question\":\"Q?\",\"type\":\"made_up\"}]}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(badType.IsFailure, "Unknown question type should be a failure");
    }
}

internal static class ClarificationSchemaTests
{
    public static void Run()
    {
        // Each stage has its own ToolsJson now (Revise / Resolve), matching the
        // MainIdeaComparator pattern. Verify both shapes independently.
        using var reviseDoc = JsonDocument.Parse(ClarificationRevisePrompts.ToolsJson);
        AssertEx.Equal(JsonValueKind.Array, reviseDoc.RootElement.ValueKind, "Revise tools_json must be a JSON array");
        AssertEx.Equal(1, reviseDoc.RootElement.GetArrayLength(), "Revise tools_json must contain exactly one tool");

        var revise = reviseDoc.RootElement[0].GetProperty("function");
        AssertEx.Equal(ClarificationSchemas.ReviseToolName, revise.GetProperty("name").GetString()!, "Revise tool name must be report_revised_questions");

        var reviseProps = revise.GetProperty("parameters").GetProperty("properties");
        var questions = reviseProps.GetProperty("questions");
        var item = questions.GetProperty("items").GetProperty("properties");
        AssertEx.True(item.TryGetProperty("type", out var typeProp), "Revise items must declare a type field");
        AssertEx.Equal(JsonValueKind.Array, typeProp.GetProperty("enum").ValueKind, "type field must be an enum");

        using var resolveDoc = JsonDocument.Parse(ClarificationResolvePrompts.ToolsJson);
        AssertEx.Equal(JsonValueKind.Array, resolveDoc.RootElement.ValueKind, "Resolve tools_json must be a JSON array");
        AssertEx.Equal(1, resolveDoc.RootElement.GetArrayLength(), "Resolve tools_json must contain exactly one tool");

        var resolve = resolveDoc.RootElement[0].GetProperty("function");
        AssertEx.Equal(ClarificationSchemas.ResolveToolName, resolve.GetProperty("name").GetString()!, "Resolve tool name must be report_resolved_concerns");

        AssertEx.True(
            ClarificationRevisePrompts.System.Contains("topic_unknown > comprehension_gap > detail_request"),
            "Revise system prompt must spell out the merge severity rule");
    }
}

internal static class TranscriptChunkBuilderTests
{
    public static void Run()
    {
        // Small budget so we can test boundary behavior.
        var builder = new TranscriptChunkBuilder(tokenBudget: 10, tokensPerWord: 1.0);

        // Feed 5 words, chunk_completed=false -> no chunk yet.
        var result = builder.Feed("one two three four five", chunkCompleted: false);
        AssertEx.True(result is null, "Should not emit chunk below budget even if we accumulate words");

        // Feed 5 more words (total 10), chunk_completed=false -> still no chunk (boundary not met).
        result = builder.Feed("six seven eight nine ten", chunkCompleted: false);
        AssertEx.True(result is null, "Should not emit chunk when budget met but chunk_completed=false");

        // Feed 1 more word (total 11), chunk_completed=true -> emit!
        result = builder.Feed("eleven", chunkCompleted: true);
        AssertEx.True(result is not null, "Should emit chunk when budget exceeded and chunk_completed=true");
        AssertEx.True(result!.Contains("one"), "Chunk should contain first word");
        AssertEx.True(result.Contains("eleven"), "Chunk should contain last word");

        // After emit, buffer is empty. Feed a few more words then flush.
        builder.Feed("twelve thirteen", chunkCompleted: false);
        var flushed = builder.Flush();
        AssertEx.True(flushed is not null, "Flush should return remaining text");
        AssertEx.True(flushed!.Contains("twelve"), "Flushed chunk should contain buffered words");

        // Flush on empty returns null.
        AssertEx.True(builder.Flush() is null, "Flush on empty buffer should return null");
    }
}
