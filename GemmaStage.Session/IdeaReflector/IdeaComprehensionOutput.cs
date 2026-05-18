using GemmaStage.Session.Native;
using GemmaStage.Session.Resilience;

namespace GemmaStage.Session.IdeaReflector;

public sealed record IdeaComprehensionOutput(string Thesis);

public sealed record IdeaComprehensionParseResult(
    string? ToolName,
    IdeaComprehensionOutput? Output,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsFailure => Error is not null;
}

public sealed record IdeaComprehensionTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    IdeaComprehensionParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson);
