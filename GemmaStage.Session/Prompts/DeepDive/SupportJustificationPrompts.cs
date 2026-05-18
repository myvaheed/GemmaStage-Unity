namespace GemmaStage.Session.Prompts.DeepDive;

public static class SupportJustificationPrompts
{
    public const bool EnableThinking = true;

    public const string System =
        "The score is computed from the chunk labels and shown under `=== COMPUTED SCORE ===`. Take the value VERBATIM from the `Score:` line; do not derive your own number.\n" +
        "Write a short verdict explaining what the support labels reveal — what claims were backed and what were not. Cite specific chunks when helpful.\n" +
        "\n" +
        "Always call exactly one tool: report_support_justification, with the supplied `value` integer (1-5) and a short `verdict`.";

    public const string UserTemplate =
        "Write the Support & Justification verdict using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with the supplied integer value and one verdict string.";

    // Short-transcript variant: scored by the model from raw transcript +
    // retellings + confusion dynamics, against the level rubric.
    public const string SystemShort =
        "Score Support & Justification on a 1-5 scale. The transcript is short (fewer than 3 chunks), so judge from the content itself rather than from chunk labels.\n" +
        "\n" +
        "Read the raw transcript, the chunk retellings, and the audience confusion dynamics. Then apply this rubric:\n" +
        "\n" +
        "  5 — Claims are backed by strong reasoning, examples, or evidence throughout.\n" +
        "  4 — Support is present, but more strong evidence would help.\n" +
        "  3 — Some support is present, but it is weak.\n" +
        "  2 — No supporting reasoning or examples.\n" +
        "  1 — No support, and the topic itself sounds fantastical or implausible.\n" +
        "\n" +
        "Always call exactly one tool: report_support_justification, with `value` (integer 1-5) and a short `verdict`.";

    public const string UserTemplateShort =
        "Score Support & Justification using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with one integer value (1-5) and one verdict string.";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_support_justification",
              "description": "Submit the Support & Justification score and verdict.",
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
