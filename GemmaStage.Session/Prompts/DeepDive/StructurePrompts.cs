namespace GemmaStage.Session.Prompts.DeepDive;

public static class StructurePrompts
{
    public const bool EnableThinking = true;

    public const string System =
        "The score is computed from the chunk labels and shown under `=== COMPUTED SCORE ===`. Take the value VERBATIM from the `Score:` line; do not derive your own number.\n" +
        "Write a short verdict explaining what the structure labels reveal and why the score fits. Cite specific chunks when helpful.\n" +
        "\n" +
        "Always call exactly one tool: report_structure, with the supplied `value` integer (1-5) and a short `verdict`.";

    public const string UserTemplate =
        "Write the Structure verdict using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with the supplied integer value and one verdict string.";

    // Short-transcript variant: when fewer than 3 chunks are available the
    // deterministic ladder cannot discriminate, so the model picks the 1-5
    // score itself from raw transcript + retellings + confusion dynamics
    // using a level rubric. No computed-score override is applied.
    public const string SystemShort =
        "Score Structure on a 1-5 scale. The transcript is short (fewer than 3 chunks), so do not require a full intro-development-conclusion arc.\n" +
        "\n" +
        "Read the raw transcript, the chunk retellings, and the audience confusion dynamics. Then apply this rubric:\n" +
        "\n" +
        "  5 — Clean, clear structure. Intro, development, and conclusion are all present.\n" +
        "  4 — Good but not perfect. An intro and a conclusion can be felt.\n" +
        "  3 — Intro, development, or conclusion is hard to identify, but the talk still feels coherent.\n" +
        "  2 — Either the intro or the conclusion is clearly missing.\n" +
        "  1 — The talk has no recognizable shape.\n" +
        "\n" +
        "Always call exactly one tool: report_structure, with `value` (integer 1-5) and a short `verdict`.";

    public const string UserTemplateShort =
        "Score Structure using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with one integer value (1-5) and one verdict string.";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_structure",
              "description": "Submit the Structure score and verdict.",
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
