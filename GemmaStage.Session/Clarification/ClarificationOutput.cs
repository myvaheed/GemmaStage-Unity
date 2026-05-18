using System.Collections.Generic;
using GemmaStage.Session.Inquirer;

namespace GemmaStage.Session.Clarification;

// One revised question produced by the Revise step. Carries the original
// concern's typing so the Final Q&A panel can color-code severity and so the
// per-round restricted Inquirer prompt can echo type back to the model.
//
// When two source concerns are merged, Revise keeps the lower id and lifts
// the type to the more severe value (topic_unknown > comprehension_gap >
// detail_request) per docs/SESSION_ARCHITECTURE.md §11.1.
public sealed record RevisedQuestion(
    long Id,
    string Question,
    InquirerConcernType Type);

public sealed record ClarificationReviseOutput(IReadOnlyList<RevisedQuestion> Questions);

public sealed record ClarificationResolveOutput(IReadOnlyList<long> ResolvedIds);
