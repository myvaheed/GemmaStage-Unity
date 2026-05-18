namespace GemmaStage.Session.Prompts;

public static class GroundTruthSummarizerReducePrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        You finalize the decomposition of a finished document. The accumulator below is the flat list of factual claims the chunked MAP pass captured. Your job is to:

          1. Deduplicate and merge near-duplicate claims (without dropping distinct facts).
          2. Infer the overarching thesis of the document — the general message it conveys (≤ 50 words).

        Always call exactly one tool: report_ground_truth_decomposition.

        Produce two fields:
          - "main_thesis": ≤ 50 words, plain language. The overarching message the document conveys. Prioritize the document's central argument; drop peripheral framing and caveats.
          - "claims": plain text, one claim per line. Every distinct factual claim on its own line. No bullets, no numbers — just the claim text. Total ≤ 250 words. Preserve all distinct claims; compress wording to fit the cap. Do NOT drop distinct claims.

        Rules:
          - Do not invent content the accumulator doesn't support.
          - Merge near-duplicates only (same fact restated in different words); KEEP ALL DISTINCT CLAIMS AS SEPARATE LINES.
          - Stay in the document's register.
        """;

    public const string UserTemplate = """
        Accumulated claims from the MAP pass (deduplicate and consolidate):
        {accumulated_claims}

        Produce: 
          - main_thesis (≤ 50 words)
          - claims (one per line, total ≤ 250 words).

        Call exactly one tool: {reduce_tool_name}.
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_ground_truth_decomposition",
              "description": "Consolidate the document's accumulated claims plus an overarching thesis (≤ 50 words).",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["main_thesis", "claims"],
                "properties": {
                  "main_thesis": { "type": "string" },
                  "claims": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
