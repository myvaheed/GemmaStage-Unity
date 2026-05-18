namespace GemmaStage.Session.Prompts;

public static class GroundTruthSummarizerMapPrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        You extract a flat list of factual claims from one chunk of a ground-truth document.
        You also receive the running list of claims captured from previous chunks. Use it to avoid restating what has already been captured — emit only NEW claims that this chunk genuinely adds.

        Always call exactly one tool: report_chunk_claims.

        Produce one field:
          - "claims": an array of strings. Each string is one self-contained factual claim — a fact, definition, comparison, recommendation, or statistic. One claim per string.

        Rules:
          - Append-only. Never restate or paraphrase a claim from the prior list, even partially. Empty array is fine when the chunk adds nothing new.
          - One claim per array entry. A claim is one fact, not a paragraph.
          - Skip pure framing, transitions, and meta-commentary.
          - Never invent content. Stay strictly in the document's register.
        """;

    public const string UserTemplate = """
        {chunk_header}

        Claims already captured from earlier chunks (do not restate any of these):
        {existing_claims}

        Current chunk text:
        {chunk_text}

        Produce: claims (only the new factual claims this chunk adds).
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_chunk_claims",
              "description": "Append-only list of new factual claims this chunk adds beyond what was captured in earlier chunks.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["claims"],
                "properties": {
                  "claims": {
                    "type": "array",
                    "items": { "type": "string" }
                  }
                }
              }
            }
          }
        ]
        """;
}
