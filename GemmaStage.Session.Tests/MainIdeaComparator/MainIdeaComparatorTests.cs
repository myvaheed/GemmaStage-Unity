using GemmaStage.Session.MainIdeaComparator;
using GemmaStage.Session.Prompts;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GemmaStage.Session.Tests.MainIdeaComparator;

internal static class MainIdeaComparatorResponseParserCoverageTests
{
    public static void Run()
    {
        var withEvidence = MainIdeaComparatorResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_claim_coverage",
                    "arguments": "{\"coverage\":\"yes\",\"evidence\":\"the paragraph restates memory consolidation\"}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(!withEvidence.IsFailure, $"yes/evidence should parse: {withEvidence.Error}");
        AssertEx.True(withEvidence.IsCoverage, "Expected coverage result");
        AssertEx.Equal(CoverageLabel.Yes, withEvidence.Coverage!.Coverage, "yes should map to CoverageLabel.Yes");
        AssertEx.True(withEvidence.Coverage.Evidence is { Length: > 0 }, "evidence should be present");

        var partial = MainIdeaComparatorResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_claim_coverage", "arguments": "{\"coverage\":\"partial\"}" } }
              ]
            }
            """);
        AssertEx.True(partial.IsCoverage, "partial should parse without evidence");
        AssertEx.Equal(CoverageLabel.Partial, partial.Coverage!.Coverage, "partial should map to CoverageLabel.Partial");
        AssertEx.True(partial.Coverage.Evidence is null, "evidence should be null when omitted");

        var no = MainIdeaComparatorResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_claim_coverage", "arguments": "{\"coverage\":\"no\"}" } }
              ]
            }
            """);
        AssertEx.True(no.IsCoverage, "no should parse");
        AssertEx.Equal(CoverageLabel.No, no.Coverage!.Coverage, "no should map to CoverageLabel.No");
    }
}

internal static class MainIdeaComparatorResponseParserThesisTests
{
    public static void Run()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_thesis_comparison",
                    "arguments": "{\"thesis_comparison\":\"The audience captured the central thesis but weakened the call to action.\"}"
                  }
                }
              ]
            }
            """;

        var parse = MainIdeaComparatorResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"Parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsThesis, "Expected a thesis comparison result");
        AssertEx.True(parse.Thesis!.ThesisComparison.Contains("captured the central thesis"),
            "thesis_comparison should round-trip");
    }
}

internal static class MainIdeaComparatorResponseParserFailureTests
{
    public static void Run()
    {
        var noToolCalls = MainIdeaComparatorResponseParser.Parse("""
            { "role": "assistant", "content": "I prefer prose." }
            """);
        AssertEx.True(noToolCalls.IsFailure, "Missing tool_calls should be a failure");

        var unknownTool = MainIdeaComparatorResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_something_else", "arguments": "{}" } }
              ]
            }
            """);
        AssertEx.True(unknownTool.IsFailure, "Unknown tool name should be a failure");

        var unknownCoverage = MainIdeaComparatorResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_claim_coverage", "arguments": "{\"coverage\":\"BOGUS\"}" } }
              ]
            }
            """);
        AssertEx.True(unknownCoverage.IsFailure, "Unknown coverage label should be a failure");

        var missingThesisComparison = MainIdeaComparatorResponseParser.Parse("""
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_thesis_comparison", "arguments": "{}" } }
              ]
            }
            """);
        AssertEx.True(missingThesisComparison.IsFailure, "Missing thesis_comparison should be a failure");

        var emptyPayload = MainIdeaComparatorResponseParser.Parse("");
        AssertEx.True(emptyPayload.IsFailure, "Empty payload should be a failure");
    }
}

internal static class MainIdeaComparatorSchemaTests
{
    public static void Run()
    {
        using var coverageDoc = JsonDocument.Parse(MainIdeaComparatorCoveragePrompts.ToolsJson);
        AssertEx.Equal(1, coverageDoc.RootElement.GetArrayLength(), "coverage tools_json must contain one tool");
        var coverageTool = coverageDoc.RootElement[0].GetProperty("function");
        AssertEx.Equal(MainIdeaComparatorSchemas.CoverageToolName,
            coverageTool.GetProperty("name").GetString()!, "Coverage tool should be report_claim_coverage");
        var coverageEnum = coverageTool
            .GetProperty("parameters")
            .GetProperty("properties")
            .GetProperty("coverage")
            .GetProperty("enum");
        var enumValues = new List<string>();
        foreach (var v in coverageEnum.EnumerateArray()) enumValues.Add(v.GetString()!);
        AssertEx.True(enumValues.Contains("yes"), "coverage enum must contain yes");
        AssertEx.True(enumValues.Contains("partial"), "coverage enum must contain partial");
        AssertEx.True(enumValues.Contains("no"), "coverage enum must contain no");
        AssertEx.Equal(3, enumValues.Count, "coverage enum must contain exactly yes/partial/no");

        using var thesisDoc = JsonDocument.Parse(MainIdeaComparatorThesisPrompts.ToolsJson);
        AssertEx.Equal(1, thesisDoc.RootElement.GetArrayLength(), "thesis tools_json must contain one tool");
        var thesisTool = thesisDoc.RootElement[0].GetProperty("function");
        AssertEx.Equal(MainIdeaComparatorSchemas.ThesisToolName,
            thesisTool.GetProperty("name").GetString()!, "Thesis tool should be report_thesis_comparison");
        var thesisProps = thesisTool.GetProperty("parameters").GetProperty("properties");
        AssertEx.True(thesisProps.TryGetProperty("thesis_comparison", out _),
            "Thesis schema must expose thesis_comparison");
    }
}

internal static class MainIdeaComparatorRecallScoringTests
{
    public static void Run()
    {
        // 2 yes + 1 partial + 1 no over 4 anchor claims → recall = 2.5/4 = 0.625
        var coverages = new[]
        {
            new ClaimCoverage("anchor1", CoverageLabel.Yes, null),
            new ClaimCoverage("anchor2", CoverageLabel.Yes, null),
            new ClaimCoverage("anchor3", CoverageLabel.Partial, null),
            new ClaimCoverage("anchor4", CoverageLabel.No, null),
        };
        var recall = MainIdeaComparatorRunner.ComputeRecall(coverages);
        AssertEx.True(Math.Abs(recall - 0.625) < 1e-9, $"Recall should be 0.625 (got {recall})");

        var allYes = MainIdeaComparatorRunner.ComputeRecall(new[]
        {
            new ClaimCoverage("anchor1", CoverageLabel.Yes, null),
            new ClaimCoverage("anchor2", CoverageLabel.Yes, null),
        });
        AssertEx.True(Math.Abs(allYes - 1.0) < 1e-9, "All-yes should yield Recall = 1.0");

        var emptyAnchor = MainIdeaComparatorRunner.ComputeRecall(Array.Empty<ClaimCoverage>());
        AssertEx.True(emptyAnchor == 0.0, "Empty coverage list should yield Recall = 0.0 (no NaN)");
    }
}

internal static class MainIdeaComparatorPromptShapeTests
{
    public static void Run()
    {
        var coveragePrompt = MainIdeaComparatorConversation.BuildCoveragePrompt(
            "Memory consolidates during sleep via hippocampal replay.",
            "The listener recalled that sleep helps the brain remember things.");
        AssertEx.True(coveragePrompt.Contains("Memory consolidates during sleep"),
            "Coverage prompt should embed the anchor claim");
        AssertEx.True(coveragePrompt.Contains("sleep helps the brain remember things"),
            "Coverage prompt should embed the audience main_idea_understanding");
        AssertEx.True(coveragePrompt.Contains(MainIdeaComparatorSchemas.CoverageToolName),
            "Coverage prompt should name the coverage tool");

        var thesisPrompt = MainIdeaComparatorConversation.BuildThesisPrompt(
            "Sleep is foundational to health.",
            "Sleep helps memory and cognitive performance.");
        AssertEx.True(thesisPrompt.Contains("Sleep is foundational"),
            "Thesis prompt should embed the anchor thesis");
        AssertEx.True(thesisPrompt.Contains("Sleep helps memory"),
            "Thesis prompt should embed the audience thesis");
        AssertEx.True(thesisPrompt.Contains(MainIdeaComparatorSchemas.ThesisToolName),
            "Thesis prompt should name the thesis tool");
    }
}

internal static class MainIdeaComparatorRunnerSkipTests
{
    public static void Run()
    {
        var skipped = MainIdeaComparatorResult.Skipped("test reason");
        AssertEx.True(!skipped.Ran, "Skipped result must not be Ran");
        AssertEx.True(skipped.Output is null, "Skipped result must carry no Output");
        AssertEx.Equal("test reason", skipped.SkipReason!, "Skip reason should round-trip");
        AssertEx.Equal(0, skipped.CoverageTurns.Count, "Skipped result must carry no coverage turns");
        AssertEx.True(skipped.ThesisTurn is null, "Skipped result must carry no thesis turn");
    }
}
