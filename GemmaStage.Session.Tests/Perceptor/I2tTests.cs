using System.Text.Json;
using GemmaStage.Session.Perceptor;
using GemmaStage.Session.Prompts;

namespace GemmaStage.Session.Tests.Perceptor;

internal static class I2tResponseParserImageTests
{
    public static void Run()
    {
        var json = """
            {
              "role": "assistant",
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_image_observation",
                    "arguments": "{\"examination\":\"Slide titled 'Memory budget' with a 3-row bar chart.\",\"notes\":[]}"
                  }
                }
              ]
            }
            """;

        var parse = I2tResponseParser.Parse(json);
        AssertEx.True(!parse.IsFailure, $"Image parse should succeed: {parse.Error}");
        AssertEx.True(parse.IsImage, "Result should report image modality");
        AssertEx.True(parse.Image is not null, "Image payload should be populated");
        AssertEx.Equal("Slide titled 'Memory budget' with a 3-row bar chart.", parse.Image!.Examination, "Examination should round-trip");
        AssertEx.Equal(0, parse.Image.Notes.Count, "Empty notes should produce empty list");
    }
}

internal static class I2tResponseParserOtherToolRejectedTests
{
    public static void Run()
    {
        var unrelatedToolInsteadOfImage = I2tResponseParser.Parse("""
            {
              "tool_calls": [
                {
                  "type": "function",
                  "function": {
                    "name": "report_idea_reflector_understanding",
                    "arguments": "{\"topic\":\"x\",\"retelling\":\"x\",\"main_idea_understanding\":\"x\"}"
                  }
                }
              ]
            }
            """);
        AssertEx.True(unrelatedToolInsteadOfImage.IsFailure, "Non-image tools must be rejected by I2T parser");
    }
}

internal static class I2tSchemaTests
{
    public static void Run()
    {
        AssertEx.True(I2tPrompts.UserTemplate.Contains("[image]"), "User template must contain the [image] marker");
        AssertEx.True(I2tPrompts.UserTemplate.Contains("{main_idea_understanding}"), "User template must contain the main_idea_understanding placeholder");

        using var doc = JsonDocument.Parse(I2tPrompts.ToolsJson);
        AssertEx.Equal(JsonValueKind.Array, doc.RootElement.ValueKind, "I2T tools_json must be a JSON array");
        AssertEx.Equal(1, doc.RootElement.GetArrayLength(), "I2T tools_json must contain exactly one tool definition (image)");

        var function = doc.RootElement[0].GetProperty("function");
        AssertEx.Equal(I2tSchemas.ImageToolName, function.GetProperty("name").GetString()!, "I2T tool must be the image observation tool");

        var parameters = function.GetProperty("parameters");
        var properties = parameters.GetProperty("properties");
        var required = parameters.GetProperty("required");
        AssertEx.True(properties.TryGetProperty("examination", out _), "Image tool must expose the examination field");
        AssertEx.Equal(1, required.GetArrayLength(), "Image tool should require only examination");
    }
}
