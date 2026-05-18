using GemmaStage.Session.Native;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.IdeaReflector;

// Audience-side recall: flat plain-text representation. Built incrementally
// each cycle by System2; thesis is filled in by IdeaComprehension at end-of-live.
public sealed record AudienceSideRecall(
    string? Topic,
    string Thesis,
    string MainIdeaUnderstanding);

// Per-cycle reflection record stored in RetellingsHistory. Topic + Retelling
// come from System1; MainIdeaUnderstanding is the post-System2 snapshot.
public sealed record IdeaReflectorReflection(
    string Topic,
    string Retelling,
    string MainIdeaUnderstanding);

public sealed record IdeaReflectorParseResult(
    string? ToolName,
    IdeaReflectorReflection? Reflection,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsFailure => Error is not null;
}

public sealed record IdeaReflectorTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    IdeaReflectorParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson,
    string? TopicAfter,
    string MainIdeaUnderstandingAfter,
    AudienceSideRecall AudienceRecallAfter,
    System1ReactorTurnResult? System1Turn,
    System2ReflectorTurnResult? System2Turn);
