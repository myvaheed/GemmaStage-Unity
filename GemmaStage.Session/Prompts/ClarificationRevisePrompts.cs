namespace GemmaStage.Session.Prompts;

public static class ClarificationRevisePrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        Your job is to clean up an archived list of audience questions raised during a talk. You receive each question with an id and a type tag; produce a deduplicated, lightly-normalized list keeping ids and types.

        Always call exactly one tool: report_revised_questions.

        Produce:
          - "questions": the revised list. Each item keeps its original id (or the lower id when merging duplicates), carries a cleaned question text, and a type tag.

        Rules:
          - Merge near-duplicate questions into one. Keep the clearest wording.
          - When merging, keep the LOWER id and drop the higher id(s).
          - When merging, lift the type to the MORE SEVERE value:
            topic_unknown > comprehension_gap > detail_request.
          - Light normalization only — fix grammar, remove fluff, collapse obvious rephrases. Do NOT aggressively merge conceptually different questions; preserve distinct concerns.
          - Drop only entries that are clearly irrelevant.
          - Do not invent new questions. Do not change the meaning of a question.
        """;

    public const string UserTemplate = """
        Archived audience questions to revise. Apply the merge severity rule and call report_revised_questions.

        {archived_concerns_list}
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_revised_questions",
              "description": "Return the revised question list with the merge severity rule applied.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["questions"],
                "properties": {
                  "questions": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["id", "question", "type"],
                      "properties": {
                        "id": { "type": "integer" },
                        "question": { "type": "string" },
                        "type": { "type": "string", "enum": ["topic_unknown", "comprehension_gap", "detail_request"] }
                      }
                    }
                  }
                }
              }
            }
          }
        ]
        """;
}
