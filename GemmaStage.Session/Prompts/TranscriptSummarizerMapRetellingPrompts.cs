namespace GemmaStage.Session.Prompts;

public static class TranscriptSummarizerMapRetellingPrompts
{
    public const bool EnableThinking = false;

    public const string System = """
        Your job is to retell one chunk of a transcribed talk in fewer words, in the speaker's register.

        Always call exactly one tool: report_chunk_retelling.

        Produce one field:
          - "retelling": say what was said in this chunk in fewer words. Not a third-person description ("the speaker explained..."). Preserve every claim, fact, and example. If the chunk was rambling, off-topic, or full of filler, retell it that way — do not clean it up.

        Continuity:
          - You will be shown the retelling of the PREVIOUS chunk (if any). Continue from where it left off — do not restart from the beginning of the talk, and do not repeat content already covered there.

        Audience Q&A rounds inside this chunk:
          - You may be shown a list of Q&A ROUNDS that took place during this chunk (each carries the audience question and the speaker's spoken answer text). When such rounds exist, render each one as a SEPARATE block inside the retelling, in the form:
              Was asked a question: "<question>". I answered: "<retelling of the answer>".
          - Place each Q&A block at the chronological position where it occurred relative to the rest of the chunk. Retell the audience's question verbatim (or near-verbatim) and compress the speaker's answer the same way you compress the rest of the transcript.

        Rules:
          - Compress, do not describe.
          - Do NOT score or evaluate. Retell only.
          - Do NOT invent content that is not in the chunk or in the listed Q&A rounds.
        """;

    public const string UserTemplate = """
        Previous chunk retelling (continue from here, do not repeat):
        {previous_retelling}

        Q&A rounds during this chunk (render each as a "Was asked... I answered..." block):
        {qa_rounds_block}

        {chunk_header}
        {chunk_text}

        Call exactly one tool: {tool_name}. Produce only "retelling".
        """;

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_chunk_retelling",
              "description": "Retell the given transcript chunk in fewer words, preserving every claim and example.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["retelling"],
                "properties": {
                  "retelling": { "type": "string" }
                }
              }
            }
          }
        ]
        """;
}
