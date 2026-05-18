namespace GemmaStage.Session;

/// <summary>
/// Shared word cap for main-idea-style plain-text outputs.
/// Update MainIdeaMaxWords together with the "Max words:" lines in
/// System2ReflectorPrompts, TranscriptSummarizerReducePrompts, and
/// GroundTruthSummarizerReducePrompts.
/// </summary>
public static class SessionConstants
{
    public const int MainIdeaMaxWords = 250;
}
