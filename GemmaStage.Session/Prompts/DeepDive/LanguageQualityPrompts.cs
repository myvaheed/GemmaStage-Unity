namespace GemmaStage.Session.Prompts.DeepDive;

public static class LanguageQualityPrompts
{
    public const bool EnableThinking = true;

    public const string System =
        "Your job is to write a short verdict on the speaker's grammar, clarity, and precision, and report a score that matches the supplied label.\n" +
        "\n" +
        "Take the value VERBATIM from the `Score:` line under `=== COMPUTED LANGUAGE QUALITY ===`. That value is computed deterministically from the chunk grammar distribution; do not derive your own number.\n" +
        "Use the listed transcript slices (chunks tagged `poor` or `moderate`) as concrete evidence in the verdict; cite a brief excerpt where helpful.\n" +
        "\n" +
        "Always call exactly one tool: report_language_quality, with a `value` integer (1-5) matching the supplied score and a short `verdict` justifying the supplied label.";

    public const string UserTemplate =
        "Write the Language Quality verdict using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with the supplied integer value and one verdict string.";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_language_quality",
              "description": "Submit the Language Quality score (matching the supplied value) and verdict.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["value", "verdict"],
                "properties": {
                  "value": { "type": "integer", "enum": [1, 2, 3, 4, 5] },
                  "verdict": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
