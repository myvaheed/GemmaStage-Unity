namespace GemmaStage.Session.Prompts;

public static class IdeaComprehensionPrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        You infer the overarching thesis the listener walked away with after a finished talk. You receive the listener's accumulated main idea understanding (a cumulative plain-text paragraph). Distill its general message into ≤ 50 words.

        Always call exactly one tool: report_idea_thesis.

        Produce one field:
          - "thesis": ≤ 50 words. Plain language. The overarching message the talk conveyed, as the listener understood it. No filler, no meta-commentary.

        Rules:
          - Infer the thesis ONLY from the paragraph below. Do not invent content the paragraph doesn't support.
          - If the paragraph is sparse, produce the best-fit thesis you can and keep it short.
        """;

    public const string UserTemplate = """
        Listener's topic: {topic}

        Listener's accumulated main idea understanding:
        {main_idea_understanding}

        Produce: thesis (≤ 50 words; the overarching message of the paragraph above).
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_idea_thesis",
              "description": "Report the overarching thesis (≤ 50 words) the listener inferred from the accumulated main idea understanding.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["thesis"],
                "properties": {
                  "thesis": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
