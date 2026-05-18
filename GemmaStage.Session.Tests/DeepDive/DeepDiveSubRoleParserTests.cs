using GemmaStage.Session.DeepDive;
using ConsistencyFocusNS = GemmaStage.Session.DeepDive.ConsistencyFocus;
using EmotionalDeliveryNS = GemmaStage.Session.DeepDive.EmotionalDelivery;
using LanguageQualityNS = GemmaStage.Session.DeepDive.LanguageQuality;
using MainIdeaClarityNS = GemmaStage.Session.DeepDive.MainIdeaClarity;
using QaHandlingNS = GemmaStage.Session.DeepDive.QaHandling;
using StructureNS = GemmaStage.Session.DeepDive.Structure;
using SupportJustificationNS = GemmaStage.Session.DeepDive.SupportJustification;

namespace GemmaStage.Session.Tests.DeepDive;

internal static class DeepDiveSubRoleParserTests
{
    public static void Run()
    {
        AcceptsValidIntegerValueAndVerdict();
        RejectsValueOutOfRange();
        RejectsZeroForCoreCriterion();
        RejectsZeroForEmotionalDelivery();
        AcceptsZeroForQaHandling();
        RejectsUnknownTool();
        RejectsMissingVerdict();
    }

    private static void AcceptsValidIntegerValueAndVerdict()
    {
        var json = """
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_main_idea_clarity",
                    "arguments": "{\"value\":4,\"verdict\":\"Clear thesis with strong examples.\"}"
                  }
                }
              ]
            }
            """;
        var parse = MainIdeaClarityNS.MainIdeaClarityResponseParser.Parse(json);
        AssertEx.True(parse.IsSuccess, $"Valid sub-role parse should succeed: {parse.Error}");
        AssertEx.Equal(DeepDiveScore.Four, parse.Criterion!.Value, "Value should round-trip");
        AssertEx.Equal("Clear thesis with strong examples.", parse.Criterion.Verdict, "Verdict should round-trip");
    }

    private static void RejectsValueOutOfRange()
    {
        var json = """
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_structure", "arguments": "{\"value\":7,\"verdict\":\"x\"}" } }
              ]
            }
            """;
        var parse = StructureNS.StructureResponseParser.Parse(json);
        AssertEx.True(parse.IsFailure, "Out-of-range value must be a failure");
    }

    private static void RejectsZeroForCoreCriterion()
    {
        var json = """
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_consistency_focus", "arguments": "{\"value\":0,\"verdict\":\"x\"}" } }
              ]
            }
            """;
        var parse = ConsistencyFocusNS.ConsistencyFocusResponseParser.Parse(json);
        AssertEx.True(parse.IsFailure, "Core criterion must reject value 0");
    }

    private static void RejectsZeroForEmotionalDelivery()
    {
        var json = """
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_emotional_delivery", "arguments": "{\"value\":0,\"verdict\":\"calm throughout\"}" } }
              ]
            }
            """;
        var parse = EmotionalDeliveryNS.EmotionalDeliveryResponseParser.Parse(json);
        AssertEx.True(parse.IsFailure, "Emotional Delivery must reject value 0 — always commit to 1-5");
    }

    private static void AcceptsZeroForQaHandling()
    {
        var json = """
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_qa_handling", "arguments": "{\"value\":0,\"verdict\":\"no rounds\"}" } }
              ]
            }
            """;
        var parse = QaHandlingNS.QaHandlingResponseParser.Parse(json);
        AssertEx.True(parse.IsSuccess, $"Q&A handling must accept value 0: {parse.Error}");
        AssertEx.Equal(DeepDiveScore.NotApplicable, parse.Criterion!.Value, "Zero should map to NotApplicable");
    }

    private static void RejectsUnknownTool()
    {
        var json = """
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_something_else", "arguments": "{\"value\":3,\"verdict\":\"x\"}" } }
              ]
            }
            """;
        var parse = SupportJustificationNS.SupportJustificationResponseParser.Parse(json);
        AssertEx.True(parse.IsFailure, "Unknown tool name must be a failure");
    }

    private static void RejectsMissingVerdict()
    {
        var json = """
            {
              "tool_calls": [
                { "type": "function", "function": { "name": "report_language_quality", "arguments": "{\"value\":4}" } }
              ]
            }
            """;
        var parse = LanguageQualityNS.LanguageQualityResponseParser.Parse(json);
        AssertEx.True(parse.IsFailure, "Missing verdict must be a failure");
    }
}
