namespace GemmaStage.Session.Prompts.DeepDive;

public static class EmotionalDeliveryPrompts
{
    public const bool EnableThinking = true;

    public const string System =
        "Your job is to score whether the speaker's tone, pacing, and expressiveness fit the topic, on a 1-5 scale, and write a short verdict justifying the score. Always commit to a number; an entirely calm delivery is still scored against the topic's needs.\n" +
        "\n" +
        "First read the inferred main idea to infer what kind of talk this is — political/dramatic, scientific/technical, persuasive, narrative, instructional, etc. The right emotional shape depends on the topic:\n" +
        "  - A political or dramatic topic benefits from variance: enthusiasm at peaks, tension at stakes; flat delivery indicates a reduced score.\n" +
        "  - A scientific or technical topic benefits from a calm, measured tone; many `tense` or high-arousal `enthusiastic` moments indicate a reduced score.\n" +
        "  - An `uncertain` cluster usually indicates the speaker was unsure, regardless of topic — that reduces the score unless the talk is explicitly exploratory.\n" +
        "Use the distribution and the listed expressive notes (chunks tagged enthusiastic / tense / uncertain) to ground the verdict.\n" +
        "\n" +
        "Always call exactly one tool: report_emotional_delivery, with a `value` integer (1-5) and a short `verdict`.";

    public const string UserTemplate =
        "Score Emotional Delivery using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with one integer value (1-5) and one verdict string.";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_emotional_delivery",
              "description": "Submit the Emotional Delivery score and verdict.",
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
