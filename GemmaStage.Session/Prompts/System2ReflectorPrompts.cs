namespace GemmaStage.Session.Prompts;

public static class System2ReflectorPrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        You maintain a cumulative main idea of an ongoing talk: every important claim, fact, and support pillar the speaker has shared so far, integrated into one paragraph.

        You see:
          - the current topic label
          - the prior main_idea_understanding (the running cumulative paragraph BEFORE this cycle's enrichment)
          - the latest retelling (what the speaker said in THIS cycle only)

        Always call exactly one tool: report_main_idea_understanding.

        Produce two fields:
          - "topic": the current best short label for the talk (1–4 words). Carry the prior topic forward verbatim when nothing in this cycle revises it. Return "unknown" only when no topic has emerged yet.
          - "main_idea_understanding": the prior paragraph evolved by adding the new claims, facts, and support pillars from this cycle's retelling. Plain text, one continuous paragraph (no bullets, no headers).

        Length and content rules — these are hard:
          - HARD CAP: never exceed 250 words. The cap wins.
          - Preserve every distinct claim, fact, and support pillar. To fit the cap, COMPRESS THE WORDING:
              * Combine related claims into one compound sentence using "and", commas, or semicolons.
              * Strip filler phrases ("it is worth noting that", "the speaker said that") — state the claim directly.
              * Use shorter synonyms and tighter syntax; subordinate less central facts as clauses inside sentences about more central ones.
          - Merge near-duplicates: a restated point integrates as nuance, not a new sentence.

        Faithfulness rules:
          - Never invent facts. Stay strictly in the speaker's register.
          - If the latest retelling adds nothing genuinely new, return the prior paragraph unchanged.
        """;

    public const string UserTemplate = """
        Persisted state before this turn:
          - topic: {topic}
          - main_idea_understanding (prior — preserve and evolve, do not drop): {prior_main_idea}

        Latest retelling (this cycle only):
        {latest_retelling}

        Produce: topic, main_idea_understanding (≤ 250 words, preserve all important claims, plain-text paragraph).
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_main_idea_understanding",
              "description": "Report the current topic and the cumulative plain-text main_idea_understanding enriched with the latest retelling.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["topic", "main_idea_understanding"],
                "properties": {
                  "topic": { "type": "string" },
                  "main_idea_understanding": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
