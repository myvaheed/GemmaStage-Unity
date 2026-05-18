namespace GemmaStage.Session.Prompts;

public static class MainIdeaComparatorThesisPrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        You compare two thesis statements about a finished talk and explain how the listener's understanding differs from the speaker's intended through-line.

        Always call exactly one tool: report_thesis_comparison.

        Produce one field:
          - "thesis_comparison": a short prose paragraph (≤ 50 words) that names what the listener got right, what they missed or weakened, and what they emphasised differently. Plain language, no bullets, no headers. Do not invent content beyond what the two theses assert.
        """;

    public const string UserTemplate = """
        Anchor thesis (from the speaker's ground-truth document):
          {anchor_thesis}

        Audience thesis (what the listener inferred):
          {audience_thesis}

        Compare them. Call exactly one tool: {thesis_tool_name}.
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_thesis_comparison",
              "description": "Compare the listener's thesis with the speaker's anchor thesis and explain the alignment / divergence in plain prose.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["thesis_comparison"],
                "properties": {
                  "thesis_comparison": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
