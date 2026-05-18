namespace GemmaStage.Session.Prompts.DeepDive;

public static class MainIdeaClarityPrompts
{
    public const bool EnableThinking = true;

    // Used when a ground-truth anchor was attached. Suggested score is derived
    // from Recall (empirically grounded: lecture-video ~60% immediate retention;
    // retrieval-practice ~75%; typical range is "half-to-two-thirds correct").
    // Model adjusts for thesis divergence — the only signal the compute step
    // cannot assess.
    public const string WithAnchorSystem =
        "A suggested score based on Recall is shown under `=== COMPUTED SCORE (based on Recall) ===`. Start from it, then read `=== MAIN IDEA COMPARATOR ===` (anchor thesis vs audience thesis, per-claim coverage with optional evidence, and Recall) and apply these adjustments:\n" +
        "  - Thesis clearly divergent (wrong central point per the MAIN IDEA COMPARATOR thesis comparison) → cap at 2.\n" +
        "  - Thesis partial/mixed → cap at 3.\n" +
        "  - Many `partial` covers at the same Recall → lean one step lower.\n" +
        "  - Persistent `medium`/`high`/`very_high` confusion → drop by 1.\n" +
        "\n" +
        "Write a short verdict describing what the audience got right (cite specific covered claims when useful), what they missed, and why.\n" +
        "\n" +
        "Always call exactly one tool: report_main_idea_clarity, with a `value` integer (1-5) and a short `verdict`.";

    // Used when no ground-truth anchor was attached. Score is judged from
    // alignment between the inferred main idea and the audience-side recall,
    // plus confusion dynamics.
    public const string NoAnchorSystem =
        "Your job is to score how clearly the speaker's core idea landed, on a 1-5 scale, and write a short verdict justifying the score. No reference document is available, so judge from delivery and listener-side signals only.\n" +
        "\n" +
        "  - Compare the `=== INFERRED MAIN IDEA FROM TRANSCRIPT ===` section to the `=== AUDIENCE-SIDE RECALL ===` section. Substantial alignment (same thesis, overlapping topics) indicates a higher score; clear divergence indicates a reduced score.\n" +
        "  - Use `=== CONFUSION DYNAMICS ===`: mostly `low` across phases means the talk was decodable (higher score); persistent `medium`/`high`/`very_high` means the listener struggled (reduced score).\n" +
        "\n" +
        "Always call exactly one tool: report_main_idea_clarity, with a `value` integer (1-5) and a short `verdict` describing what worked, what failed, and why.";

    public const string UserTemplate =
        "Score Main Idea Clarity using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with one integer value (1-5) and one verdict string.";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_main_idea_clarity",
              "description": "Submit the Main Idea Clarity score and verdict.",
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
