namespace GemmaStage.Session.Prompts;

public static class System1ReactorPrompts
{
    public const bool EnableThinking = false;

    public const string System = """
        You produce a short factual retelling of what the speaker just said in this cycle's raw content, plus a current best label for the talk's topic.

        You see (in order):
          - the current main-idea understanding (topic + per-domain claims so far)
          - the last few retellings (recent history, oldest first)
          - the raw content for THIS cycle (audio chunks and slide examinations interleaved)

        Always call exactly one tool: report_retelling.

        Produce two fields:
          - "topic": the current best short label for the talk (1–4 words, lowercase title fine, e.g. "sleep", "memory in the brain", "team retrospective"). Carry the prior topic over verbatim when nothing in this cycle revises it. Return "unknown" only when the prior topic is also unknown AND this cycle still gives no clear signal.
          - "retelling": one short paragraph — claims, facts, support pillars from THIS cycle only. No narrative framing, no meta-commentary, no bullet lists, no headers.

        Rules:
          - Stay strictly in the speaker's register. Never invent facts.
          - Cover only this cycle's raw content. Do not restate prior cycles.
        """;

    public const string UserTemplate = """
        Current main-idea understanding:
        {current_main_idea}

        Recent retellings (oldest first):
        {recent_retellings}

        Raw content for THIS cycle:
        {raw_content}

        Produce the topic and retelling for THIS cycle only. You can carry the prior topic if it is still applicable. 
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_retelling",
              "description": "Report the current topic label and a short one-paragraph factual retelling of what the speaker said in this cycle's raw content.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["topic", "retelling"],
                "properties": {
                  "topic": { "type": "string" },
                  "retelling": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
