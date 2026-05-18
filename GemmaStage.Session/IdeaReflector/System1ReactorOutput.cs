using GemmaStage.Session.Native;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.IdeaReflector;

public sealed record System1ReactorOutput(string Topic, string Retelling);

public sealed record System1ReactorParseResult(
    string? ToolName,
    System1ReactorOutput? Output,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsFailure => Error is not null;
}

public sealed record System1ReactorTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    System1ReactorParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson);
