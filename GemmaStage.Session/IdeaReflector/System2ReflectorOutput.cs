using GemmaStage.Session.Native;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.IdeaReflector;

public sealed record System2ReflectorOutput(string Topic, string MainIdeaUnderstanding);

public sealed record System2ReflectorParseResult(
    string? ToolName,
    System2ReflectorOutput? Output,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsFailure => Error is not null;
}

public sealed record System2ReflectorTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    System2ReflectorParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson);
