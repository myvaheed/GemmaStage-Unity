using System.Collections.Generic;
using GemmaStage.Session.Inquirer;

namespace GemmaStage.Session.Clarification;

public static class ClarificationSchemas
{
    public const string ReviseToolName = "report_revised_questions";
    public const string ResolveToolName = "report_resolved_concerns";

    public const int QuestionsMaxItems = 20;
    public const int ResolvedIdsMaxItems = 20;

    public const string ConcernTypeTopicUnknown = InquirerSchemas.ConcernTypeTopicUnknown;
    public const string ConcernTypeComprehensionGap = InquirerSchemas.ConcernTypeComprehensionGap;
    public const string ConcernTypeDetailRequest = InquirerSchemas.ConcernTypeDetailRequest;

    public static readonly IReadOnlyList<string> ConcernTypeLabels = new[]
    {
        ConcernTypeTopicUnknown,
        ConcernTypeComprehensionGap,
        ConcernTypeDetailRequest,
    };
}
