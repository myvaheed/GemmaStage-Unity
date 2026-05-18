using System.Collections.Generic;

namespace GemmaStage.Session.Clarification;

// End-to-end result of the Clarification phase.
//
// Ran:                  true when Clarification actually executed. False
//                       means the concern archive was empty (nothing to do).
// OriginalArchivedCount number of archived concerns fed into Revise.
// RevisedQuestions      deduplicated / type-merged list returned by Revise.
//                       Always carries the source ids and the most-severe
//                       type per merge group.
// ResolvedIds           ids of revised questions marked resolved by the
//                       transcript walk. Union across all chunks. Important
//                       when an early-overflow concern was archived before
//                       Inquirer could see the talk's later answer.
// UnresolvedQuestions   revised questions that remained open after the walk
//                       — these become the Final Q&A panel set.
// ReviseTurn            raw turn result for the Revise step.
// ResolveTurns          raw turn results for each chunk Resolve step.
public sealed record ClarificationResult(
    bool Ran,
    int OriginalArchivedCount,
    IReadOnlyList<RevisedQuestion> RevisedQuestions,
    IReadOnlyList<long> ResolvedIds,
    IReadOnlyList<RevisedQuestion> UnresolvedQuestions,
    ClarificationTurnResult? ReviseTurn,
    IReadOnlyList<ClarificationTurnResult> ResolveTurns)
{
    public static ClarificationResult Skipped { get; } = new(
        Ran: false,
        OriginalArchivedCount: 0,
        RevisedQuestions: System.Array.Empty<RevisedQuestion>(),
        ResolvedIds: System.Array.Empty<long>(),
        UnresolvedQuestions: System.Array.Empty<RevisedQuestion>(),
        ReviseTurn: null,
        ResolveTurns: System.Array.Empty<ClarificationTurnResult>());
}
