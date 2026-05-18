using System.Collections.Generic;

namespace GemmaStage.Session.Perceptor;

// ASR-only schema. The conversation that uses this schema is created fresh
// per audio chunk, so the model never sees a prior transcript and cannot be
// primed to copy or extend it.
public static class AsrSchemas
{
    public const string AudioToolName = "report_audio_observation";

    public const int NotesMaxItems = 3;

    public static readonly IReadOnlyList<string> EmotionLabels = new[]
    {
        "calm",
        "enthusiastic",
        "tense",
        "uncertain",
    };

    public static readonly IReadOnlyList<string> ClarityLabels = new[]
    {
        "messy",
        "normal",
    };

    public static readonly IReadOnlyList<string> GrammarLabels = new[]
    {
        "poor",
        "moderate",
        "good",
        "excellent",
    };
}
