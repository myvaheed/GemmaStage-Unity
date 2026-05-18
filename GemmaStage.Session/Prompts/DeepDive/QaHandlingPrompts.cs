namespace GemmaStage.Session.Prompts.DeepDive;

public static class QaHandlingPrompts
{
    public const bool EnableThinking = true;

    public const string System =
        "Your job is to score how effectively the speaker handled questions, on a 0-5 scale, and write a short verdict justifying the score. Use 0 (N/A) only when no Q&A rounds occurred.\n" +
        "\n" +
        "Each Q&A round in the input is wrapped between `<begin_qa ...>` and `</end_qa>` tags carrying:\n" +
        "  - phase: `Live` (mid-talk) or `Final` (after the talk).\n" +
        "  - resolution: `Resolved` if the speaker's answer addressed the underlying concern, `NotAnswered` if it did not.\n" +
        "Each round contains the question text and the speaker's answer.\n" +
        "\n" +
        "Score guidance:\n" +
        "  - All `Resolved` rounds with substantive, on-question answers: high score.\n" +
        "  - Mixed: some `Resolved` and some `NotAnswered`, partial answers: middling score.\n" +
        "  - Mostly `NotAnswered` or evasive/off-topic answers: low score.\n" +
        "Cite specific rounds in the verdict when describing what worked and what did not.\n" +
        "\n" +
        "Always call exactly one tool: report_qa_handling, with a `value` integer (0-5) and a short `verdict`.\n" +
        "When value is 0, the verdict must explain in one sentence why no Q&A handling could be evaluated.";

    public const string UserTemplate =
        "Score Q&A Handling using the data below.\n\n" +
        "{prefill}\n\n" +
        "Now call {tool_name} with one integer value (0-5) and one verdict string.";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_qa_handling",
              "description": "Submit the Q&A Handling score and verdict.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["value", "verdict"],
                "properties": {
                  "value": { "type": "integer", "enum": [0, 1, 2, 3, 4, 5] },
                  "verdict": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
