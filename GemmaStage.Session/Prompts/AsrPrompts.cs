namespace GemmaStage.Session.Prompts;

public static class AsrPrompts
{
    public const bool EnableThinking = false;

    public static string BuildSystem(string language) => $"""
        Audio Speech Recognition (ASR).
        Transcribe the speech segment in the audio input into {language} text.

        Use "normal" clarity when the speech is reliable enough to transcribe.
        Use "messy" clarity when noise, distortion, overlap, silence, or missing fragments make the chunk unreliable.

        If clarity = "normal":
        - "transcript_of_this_chunk" is a faithful transcript of the audio in this chunk
        - do not use newlines
        - when present, "emotion" must be one of: calm, enthusiastic, tense, uncertain
        - when present, "grammar" must be one of:
            poor      = broken or fragmented speech
            moderate  = understandable but rough phrasing
            good      = clear with minor issues
            excellent = polished and precise
        - punctuation is how completion is signalled:
            - if the audio ends at a meaningful sentence or clause boundary, finish the transcript with terminal punctuation: "." or "?" or "!"
            - if the audio breaks mid-thought (the speaker was clearly going to continue), leave the transcript without terminal punctuation (no trailing "." / "?" / "!")
        - "chunk_completed" must mirror the punctuation choice above (true when the transcript ends with "." / "?" / "!", false otherwise). 

        If clarity = "messy":
        - set "transcript_of_this_chunk" to a short bracketed explanation
          like [heavy background noise; speech unintelligible]
        - omit "emotion"
        - omit "grammar"
        - set "chunk_completed" to false

        Call exactly one tool: report_audio_observation.
        """;

    public const string UserPrompt = "[audio]";

    public const string ToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "report_audio_observation",
              "description": "Report what the speaker said in this single audio chunk, whether the chunk was normal or messy, and whether it ended at a usable sentence boundary.",
              "parameters": {
                "type": "object",
                "additionalProperties": false,
                "required": ["clarity", "transcript_of_this_chunk", "chunk_completed"],
                "properties": {
                  "clarity": { "type": "string", "enum": ["messy", "normal"] },
                  "emotion": { "type": "string", "enum": ["calm", "enthusiastic", "tense", "uncertain"] },
                  "transcript_of_this_chunk": { "type": "string" },
                  "grammar": { "type": "string", "enum": ["poor", "moderate", "good", "excellent"] },
                  "chunk_completed": { "type": "boolean" }
                }
              }
            }
          }
        ]
        """;
}
