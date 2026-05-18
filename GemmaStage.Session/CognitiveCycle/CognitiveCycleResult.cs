using GemmaStage.Session.IdeaReflector;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.Stores;

namespace GemmaStage.Session.CognitiveCycle;

public sealed record CognitiveCycleResult(
    int Index,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<RawContentSegment> RawContent,
    IdeaReflectorTurnResult? IdeaReflectorTurn,
    InquirerTurnResult? InquirerTurn,
    InquirerStateSnapshot StateBefore,
    InquirerStateSnapshot StateAfter,
    CycleQueueMetrics Queue,
    string? Error)
{
    public TimeSpan Duration => CompletedAt - StartedAt;

    public bool IsFailure => Error is not null;
}

public sealed record CycleQueueMetrics(
    int DepthAtPause,
    int Drained,
    TimeSpan DrainTime);
