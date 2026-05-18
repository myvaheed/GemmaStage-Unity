namespace GemmaStage.Session.Prompts;

public static class TranscriptSummarizerMapSignalsPrompts
{
    public const bool EnableThinking = false;

    public const string System = """
        Your job is to extract local communication signals from one chunk of a transcribed talk. You receive prior-chunk summaries (so you can judge consistency relative to what came before) and the new chunk text.

        Always call exactly one tool: report_chunk_signals.

        For the current chunk only, produce:
          - "structure": one of intro, development, conclusion, unclear.
          - "consistency": one of consistent, minor_drift, major_drift, judged relative to the prior-chunk summaries you were given.
          - "support": one of none, weak, moderate, strong.
          - "notes": optional short observations (max 3, each ≤ 1 sentence). Use [] when nothing stands out.

        Rules:
          - Tag the chunk; do NOT retell it.
          - Do NOT invent content that is not in the chunk.
        """;

    public const string UserTemplate = """
        Prior chunk retellings (most recent last):
        {prior_chunk_retellings}

        {chunk_header}
        {chunk_text}

        Focus on ONLY the current chunk. Tag structure, consistency (vs prior chunks), support, and optional notes.

        Call exactly one tool: {tool_name}.
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_chunk_signals",
              "description": "Tag the given transcript chunk with structure, consistency-vs-prior, support, and optional short notes.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["structure", "consistency", "support"],
                "properties": {
                  "structure": { "type": "string", "enum": ["intro", "development", "conclusion", "unclear"] },
                  "consistency": { "type": "string", "enum": ["consistent", "minor_drift", "major_drift"] },
                  "support": { "type": "string", "enum": ["none", "weak", "moderate", "strong"] },
                  "notes": {
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
