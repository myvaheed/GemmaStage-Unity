using System.Collections.Generic;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.Perceptor;

public enum PerceptorClarity
{
    Messy,
    Normal,
}

public enum PerceptorGrammar
{
    Poor,
    Moderate,
    Good,
    Excellent,
}

public enum PerceptorEmotion
{
    Calm,
    Enthusiastic,
    Tense,
    Uncertain,
}

public sealed record PerceptorAudioOutput(
    PerceptorClarity Clarity,
    PerceptorEmotion? Emotion,
    string Transcript,
    PerceptorGrammar? Grammar,
    bool ChunkCompleted,
    IReadOnlyList<string> Notes);

public sealed record PerceptorImageOutput(
    string Examination,
    IReadOnlyList<string> Notes);

// Unified parse result used by AsrConversation (audio variant) and
// I2tConversation (image variant). Exactly one of Audio / Image is populated
// on success, both are null on failure. Per-cycle retellings are produced
// by the IdeaReflector role and live in its own parse-result type.
public sealed record PerceptorParseResult(
    string? ToolName,
    PerceptorAudioOutput? Audio,
    PerceptorImageOutput? Image,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsAudio => Audio is not null;
    public bool IsImage => Image is not null;
    public bool IsFailure => Error is not null;
}
