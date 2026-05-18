namespace GemmaStage.Session.Prompts;

public static class MainIdeaComparatorCoveragePrompts
{
    public const bool EnableThinking = false;

    public const string System = """
        Your job is to judge whether the listener's main_idea_understanding paragraph covers a single anchor claim from the speaker's ground-truth document. The paragraph is a cumulative plain-text summary of what the listener recalled across the whole talk.

        Always call exactly one tool: report_claim_coverage.

        Produce:
          - "coverage": one of
              * "yes"     — the paragraph asserts the same fact as the anchor claim, even if paraphrased
              * "partial" — the paragraph touches the same topic but is missing scope, nuance, qualification, or specificity
              * "no"      — the paragraph does not cover this fact at all
          - "evidence": OPTIONAL. A short quote or paraphrase from the paragraph that supports the coverage label. Omit when coverage is "no", or when no clean span exists. ≤ 30 words.

        Rules:
          - Surface restatements count as coverage. Synonymy and paraphrase count. Strict word-matching is not required.
          - Do NOT invent evidence. If you cannot ground a quote in the paragraph text, omit "evidence".
          - Judge only this single anchor claim. Other facts the paragraph contains are irrelevant.
        """;

    public const string UserTemplate = """
        Anchor claim (one fact from the ground-truth document):
          {anchor_claim}

        Listener's main_idea_understanding (the paragraph to look in):
          {audience_main_idea}

        Judge whether the paragraph covers this anchor claim. Call exactly one tool: {coverage_tool_name}.
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_claim_coverage",
              "description": "Judge whether the listener's main_idea_understanding paragraph covers one anchor claim from the ground-truth document.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["coverage"],
                "properties": {
                  "coverage": { "type": "string", "enum": ["yes", "partial", "no"] },
                  "evidence": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
