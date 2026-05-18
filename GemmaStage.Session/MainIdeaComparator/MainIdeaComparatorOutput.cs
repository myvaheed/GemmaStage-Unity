using System.Collections.Generic;

namespace GemmaStage.Session.MainIdeaComparator;

public enum CoverageLabel
{
    Yes,
    Partial,
    No,
}

// One per GT claim. The model says whether the audience's
// main_idea_understanding paragraph supports this claim, with optional
// supporting excerpt.
public sealed record ClaimCoverage(
    string AnchorClaim,
    CoverageLabel Coverage,
    string? Evidence);

public sealed record MainIdeaComparatorOutput(
    string AnchorThesis,
    IReadOnlyList<string> AnchorClaims,
    string AudienceThesis,
    string AudienceMainIdeaUnderstanding,
    string ThesisComparison,
    IReadOnlyList<ClaimCoverage> ClaimCoverages,
    double Recall);
