using System.Text.Json;
using GemmaStage.Session.Prompts.DeepDive;

namespace GemmaStage.Session.Tests.DeepDive;

internal static class DeepDiveSubRolePromptTests
{
    public static void Run()
    {
        // Each sub-role prompt must define exactly one tool whose name
        // matches the sub-role's schema constant. The shared schema shape
        // is value (integer) + verdict (string), with the value range
        // tightened per criterion.

        AssertOneTool(MainIdeaClarityPrompts.ToolsJson, "report_main_idea_clarity", acceptZero: false);
        AssertOneTool(StructurePrompts.ToolsJson, "report_structure", acceptZero: false);
        AssertOneTool(ConsistencyFocusPrompts.ToolsJson, "report_consistency_focus", acceptZero: false);
        AssertOneTool(SupportJustificationPrompts.ToolsJson, "report_support_justification", acceptZero: false);
        AssertOneTool(LanguageQualityPrompts.ToolsJson, "report_language_quality", acceptZero: false);
        AssertOneTool(EmotionalDeliveryPrompts.ToolsJson, "report_emotional_delivery", acceptZero: false);
        AssertOneTool(QaHandlingPrompts.ToolsJson, "report_qa_handling", acceptZero: true);

        // Main Idea Clarity is the only sub-role with two prompt variants.
        AssertEx.True(
            MainIdeaClarityPrompts.WithAnchorSystem != MainIdeaClarityPrompts.NoAnchorSystem,
            "WithAnchor and NoAnchor variants must differ");
        AssertEx.True(
            MainIdeaClarityPrompts.WithAnchorSystem.Contains("MAIN IDEA COMPARATOR"),
            "WithAnchor must reference the comparator section it will see");
        AssertEx.True(
            !MainIdeaClarityPrompts.NoAnchorSystem.Contains("MAIN IDEA COMPARATOR"),
            "NoAnchor must not reference a section that won't be rendered");

        // Each system prompt must avoid C# field names and (§...) section refs.
        AssertNoCSharpFieldRefs(MainIdeaClarityPrompts.WithAnchorSystem, "MainIdeaClarity.WithAnchor");
        AssertNoCSharpFieldRefs(MainIdeaClarityPrompts.NoAnchorSystem, "MainIdeaClarity.NoAnchor");
        AssertNoCSharpFieldRefs(StructurePrompts.System, "Structure");
        AssertNoCSharpFieldRefs(StructurePrompts.SystemShort, "Structure.Short");
        AssertNoCSharpFieldRefs(ConsistencyFocusPrompts.System, "ConsistencyFocus");
        AssertNoCSharpFieldRefs(ConsistencyFocusPrompts.SystemShort, "ConsistencyFocus.Short");
        AssertNoCSharpFieldRefs(SupportJustificationPrompts.System, "Support");
        AssertNoCSharpFieldRefs(SupportJustificationPrompts.SystemShort, "Support.Short");
        AssertNoCSharpFieldRefs(LanguageQualityPrompts.System, "LanguageQuality");
        AssertNoCSharpFieldRefs(EmotionalDeliveryPrompts.System, "Emotional");
        AssertNoCSharpFieldRefs(QaHandlingPrompts.System, "QaHandling");
    }

    private static void AssertOneTool(string toolsJson, string expectedName, bool acceptZero)
    {
        using var doc = JsonDocument.Parse(toolsJson);
        AssertEx.Equal(1, doc.RootElement.GetArrayLength(), $"{expectedName}: tools_json must define one tool");
        var fn = doc.RootElement[0].GetProperty("function");
        AssertEx.Equal(expectedName, fn.GetProperty("name").GetString()!, $"Tool name must be {expectedName}");
        var props = fn.GetProperty("parameters").GetProperty("properties");
        AssertEx.True(props.TryGetProperty("value", out _), $"{expectedName}: schema must expose value");
        AssertEx.True(props.TryGetProperty("verdict", out _), $"{expectedName}: schema must expose verdict");

        var enumArr = props.GetProperty("value").GetProperty("enum");
        var first = enumArr[0].GetInt32();
        AssertEx.Equal(acceptZero ? 0 : 1, first, $"{expectedName}: value enum must start at {(acceptZero ? 0 : 1)}");
    }

    private static void AssertNoCSharpFieldRefs(string system, string label)
    {
        AssertEx.True(!system.Contains("transcript_summary"), $"{label}: prompt must not reference C# field names");
        AssertEx.True(!system.Contains("(§"), $"{label}: prompt must not include doc section refs");
    }
}
