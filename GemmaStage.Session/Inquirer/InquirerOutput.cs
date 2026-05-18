using System.Collections.Generic;
using GemmaStage.Session.Native;

namespace GemmaStage.Session.Inquirer;

public enum InquirerConfusionScore
{
    Low,
    Medium,
    High,
    VeryHigh,
}

public enum InquirerConcernType
{
    TopicUnknown,
    ComprehensionGap,
    DetailRequest,
}

public enum RemovedConcernCause
{
    Resolved,
    Irrelevant,
}

public sealed record RemovedConcern(long Id, RemovedConcernCause Cause, string Note);

public sealed record InquirerConcern(long Id, string Question, InquirerConcernType Type, string? TypeReason = null);

public sealed record InquirerReflection(
    IReadOnlyList<InquirerConcern> NewConcerns,
    IReadOnlyList<RemovedConcern> RemovedConcerns);

public sealed record InquirerStateSnapshot(
    string? Topic,
    string? MainIdeaUnderstanding,
    InquirerConfusionScore? ConfusionScore,
    IReadOnlyList<InquirerConcern> Concerns);

public sealed record ArchivedConcern(long Id, string Question, InquirerConcernType Type);

public sealed record InquirerTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    InquirerParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson,
    InquirerStateSnapshot State,
    IReadOnlyList<ArchivedConcern> ArchivedConcerns);
