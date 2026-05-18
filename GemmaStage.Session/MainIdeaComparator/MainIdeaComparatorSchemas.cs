using System.Collections.Generic;

namespace GemmaStage.Session.MainIdeaComparator;

public static class MainIdeaComparatorSchemas
{
    public const string CoverageToolName = "report_claim_coverage";
    public const string ThesisToolName = "report_thesis_comparison";

    public static readonly IReadOnlyList<string> CoverageLabels = new[]
    {
        "yes",
        "partial",
        "no",
    };
}
