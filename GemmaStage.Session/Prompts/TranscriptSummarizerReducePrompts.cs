namespace GemmaStage.Session.Prompts;

public static class TranscriptSummarizerReducePrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        Your job is to read ordered per-chunk retellings of a talk and synthesize the talk's inferred main idea.

        Always call exactly one tool: report_transcript_summary.

        Produce:
          - "inferred_main_idea_from_transcript": the talk's core idea plus its load-bearing supporting points, claims, and facts.

        Length and content rules — these are hard:
          - HARD CAP: never exceed 250 words. The cap wins.
          - Preserve every distinct claim, fact, and support pillar visible in the retellings. To fit the cap, COMPRESS THE WORDING:
              * Combine related claims into one compound sentence using "and", commas, or semicolons.
              * Strip filler ("it is worth noting that", "the speaker said that") — state the claim directly.
              * Use shorter synonyms and tighter syntax; subordinate less central facts as clauses.
          - Merge near-duplicates: if multiple retellings repeat the same point, state it once.

        Stay strictly grounded in what the retellings say. Do not invent claims, examples, or terminology that the retellings do not contain.
        """;

    public const string UserTemplate = """
        Per-chunk retellings from the talk, in order:
        {map_outputs}

        Synthesize the inferred main idea of the talk.

        Call exactly one tool: {reduce_tool_name}.
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_transcript_summary",
              "description": "Report the inferred main idea of the talk, synthesized from its per-chunk retellings.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["inferred_main_idea_from_transcript"],
                "properties": {
                  "inferred_main_idea_from_transcript": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
