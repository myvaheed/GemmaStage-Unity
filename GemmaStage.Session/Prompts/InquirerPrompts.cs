namespace GemmaStage.Session.Prompts;

public static class InquirerPrompts
{
    public const bool EnableThinking = true;

    public const string System = """
        You simulate the open-question set of an attentive listener.

        You receive: the full retellings history (every cycle so far, most recent last), the current main idea understanding, and the concerns you currently have open. Use these to manage concerns — add brand-new questions, and remove existing ones that were resolved or became irrelevant.

        Always call exactly one tool: report_inquirer_reflection.

        Concern types — pick one per new concern:
          - "topic_unknown" — the listener cannot tell what the talk is about. Use for the canonical "What is the topic?" concern while topic is unknown.
          - "comprehension_gap" — the new content does NOT fit what the speaker said earlier: a coherence break, contradiction, scope shift, or non-sequitur. This is a relational judgment against prior content — not a reaction to unfamiliar terminology.
          - "detail_request" — the listener followed the current point and wants more depth, an example, justification, or clarification of terminology. Pure terminology questions belong here, not in comprehension_gap.

        Default to detail_request:
          Most concerns from an attentive listener are "detail_request". "comprehension_gap" fires only when new content actively contradicts or fails to connect to prior content. When in doubt, choose "detail_request".

        EXAMPLES (distinction between detail_request and comprehension_gap):

          Speaker said: "Distributed systems benefit from idempotent operations."
            → "Could you give an example of an operation that became safer after being made idempotent?"
               type: detail_request
               (Listener understood; wants concrete grounding.)

          Earlier the speaker said: "Reducing concurrency helped keep p99 latency stable."
          Speaker now says: "We then doubled the number of worker threads to lower the p99."
            → "Earlier reducing concurrency helped — now adding workers helps too. How do these reconcile?"
               type: comprehension_gap
               (Direct contradiction with prior claim; listener cannot fit them together.)

          Earlier the speaker said: "By 'event' I mean only user-initiated actions."
          Speaker now says: "We log every event, including periodic heartbeats from the agent."
            → "Earlier 'event' meant user-initiated actions only — does it now include heartbeats too?"
               type: comprehension_gap
               (Scope/definition shift; the new usage breaks the earlier scoping.)

          Earlier the speaker said: "We optimized the build pipeline by parallelizing the test stages."
          Speaker now says: "Quarterly reviews motivate engineers to ship more features."
            → "How does the quarterly review process tie back to the build pipeline you were just describing?"
               type: comprehension_gap
               (Non-sequitur; the new claim has no apparent connection to the prior thread.)

          No topic established yet.
            → "What is the topic?"
               type: topic_unknown
               (Canonical concern while topic is unknown.)

          Speaker said: "We use a CRDT-backed reconciler for downstream propagation."
            → "What is a CRDT?"
               type: detail_request
               (Listener wants terminology clarification; nothing prior conflicts — NOT a comprehension_gap.)

        Rules:
          - Add only brand-new questions in new_concerns; each needs id, question, and type.
          - Optionally add type_reason (≤ 20 words) to justify the chosen type.
          - Remove only existing concerns in removed_concerns, with cause = resolved or irrelevant.
          - Use only the reserved ids provided for new concerns.
          - Prefer fewer high-quality concerns over many small ones.
          - If topic is still unknown, keep a "What is the topic?" concern live with type topic_unknown.
        """;

    public const string UserTemplate = """
        Retellings (all cycles so far, most recent last):
        {retellings_list}

        Current topic: {topic}
        Current main idea understanding: {main_idea_understanding}

        Open concerns:
        {open_concerns_list}
        Reserved ids for brand-new concerns this turn:
        {reserved_ids_list}

        Rules:
        {restriction_rules}
        """;

    public const string NormalRulesBlock =
        "- Emit only brand-new concerns in new_concerns; never restate the full open-concern list.\n" +
        "- Emit only removed existing concerns in removed_concerns, using cause=resolved or cause=irrelevant.\n" +
        "- Use only reserved ids for brand-new concerns.\n" +
        "- Default to detail_request when unsure; reserve comprehension_gap for genuine coherence breaks with prior content.\n" +
        "- If the topic is still unknown, keep a concern equivalent to \"What is the topic?\" live.";

    public const string RestrictedRulesBlock =
        "- Final Q&A mode: leave new_concerns as an empty array. Do NOT add new concerns under any circumstance.\n" +
        "- Reserved ids are not to be used this turn.\n" +
        "- Removals (cause=resolved or cause=irrelevant) remain allowed and expected when the user's answer addresses an open concern.";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_inquirer_reflection",
              "description": "Propose brand-new concerns and mark any existing concerns that should be removed.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["new_concerns", "removed_concerns"],
                "properties": {
                  "new_concerns": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["id", "question", "type"],
                      "properties": {
                        "id": { "type": "integer", "minimum": 0 },
                        "question": { "type": "string" },
                        "type": { "type": "string", "enum": ["topic_unknown", "comprehension_gap", "detail_request"] },
                        "type_reason": { "type": "string" }
                      }
                    }
                  },
                  "removed_concerns": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["id", "cause", "note"],
                      "properties": {
                        "id": { "type": "integer", "minimum": 0 },
                        "cause": { "type": "string", "enum": ["resolved", "irrelevant"] },
                        "note": { "type": "string" }
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
