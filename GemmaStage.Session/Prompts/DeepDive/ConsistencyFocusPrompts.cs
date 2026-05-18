namespace GemmaStage.Session.Prompts.DeepDive;

public static class ConsistencyFocusPrompts
{
    public const bool EnableThinking = true;

    public const string System =
        "Your job is to score whether the talk stays on topic and develops logically without contradictions, on a 1-5 scale, and write a short verdict justifying the score.\n" +
        "\n" +
        "The score is computed from the chunk labels and shown under `=== COMPUTED SCORE ===`. Take the value VERBATIM from the `Score:` line; do not derive your own number.\n" +
        "Write a short verdict explaining where the talk held focus and where it drifted. Cite specific chunks when helpful.\n" +
        "\n" +
        "Always call exactly one tool: report_consistency_focus, with the supplied `value` integer (1-5) and a short `verdict`.";

    public const string UserTemplate =
        "Write the Consistency & Focus verdict using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with the supplied integer value and one verdict string.";

    // Short-transcript variant: scored by the model from raw transcript +
    // retellings + confusion dynamics, against the level rubric.
    public const string SystemShort =
        "Score Consistency & Focus on a 1-5 scale. The transcript is short (fewer than 3 chunks), so judge from the content itself rather than from chunk labels.\n" +
        "\n" +
        "Read the raw transcript, the chunk retellings, and the audience confusion dynamics. Then apply this rubric:\n" +
        "\n" +
        "  5 — Every part builds on the previous one and enriches the topic.\n" +
        "  4 — Mostly focused. The speaker drifts occasionally.\n" +
        "  3 — Drifts are clearly noticeable.\n" +
        "  2 — Most of the talk drifts off topic.\n" +
        "  1 — No focus or consistency at all.\n" +
        "\n" +
        "Always call exactly one tool: report_consistency_focus, with `value` (integer 1-5) and a short `verdict`.";

    public const string UserTemplateShort =
        "Score Consistency & Focus using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with one integer value (1-5) and one verdict string.";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_consistency_focus",
              "description": "Submit the Consistency & Focus score and verdict.",
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
