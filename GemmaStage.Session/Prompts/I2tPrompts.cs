namespace GemmaStage.Session.Prompts;

public static class I2tPrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        Your job is to describe what is shown in a single image (a slide, whiteboard, or diagram from a presentation). The talk's current main idea is provided as context — use it to frame what the visual is communicating, but keep your description grounded in what is actually visible.

        Always call exactly one tool: report_image_observation.

        Produce:
          - "examination": a compact, concrete description of only what is visible in the image, including any visible text. Do not invent context that is not in the image.
        """;

    public const string UserTemplate =
        "Current main idea of the talk so far:\n{main_idea_understanding}\n\n" +
        "[image]";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_image_observation",
              "description": "Report what is shown in the most recent slide, whiteboard, or diagram.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["examination"],
                "properties": {
                  "examination": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
