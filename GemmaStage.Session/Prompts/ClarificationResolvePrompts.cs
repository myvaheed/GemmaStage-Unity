namespace GemmaStage.Session.Prompts;

public static class ClarificationResolvePrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        Your job is to scan one chunk of a talk's transcript and decide which audience questions from the supplied list the chunk plainly answers.

        Always call exactly one tool: report_resolved_concerns.

        Produce:
          - "resolved_ids": the ids of questions the chunk clearly and directly answers. Use an empty array if none are answered.

        Rules:
          - Mark a question resolved only when the chunk plainly addresses it.
          - If unsure, leave the question unresolved.
          - Do not invent ids. Use only ids from the supplied list.
        """;

    public const string UserTemplate = """
        Transcript chunk:
        {chunk_text}

        Remaining questions:
        {remaining_questions_list}

        Call report_resolved_concerns with the ids of any questions answered by the chunk above. Use an empty array if none are answered.
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_resolved_concerns",
              "description": "Report which question ids are answered by the given transcript chunk.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["resolved_ids"],
                "properties": {
                  "resolved_ids": {
                    "type": "array",
                    "items": { "type": "integer" }
                  }
                }
              }
            }
          }
        ]
        """;
}
