using System.Collections.Generic;

namespace GemmaStage.Session.Inquirer;

public static class InquirerSchemas
{
    public const string ReflectionToolName = "report_inquirer_reflection";
    public const string TopicConcernQuestion = "What is the topic?";

    public const int ConcernMaxItems = 10;
    public const int NewConcernReservationCount = 3;

    public static readonly IReadOnlyList<string> ConfusionLabels = new[]
    {
        "low",
        "medium",
        "high",
        "very_high",
    };

    public const string ConcernTypeTopicUnknown = "topic_unknown";
    public const string ConcernTypeComprehensionGap = "comprehension_gap";
    public const string ConcernTypeDetailRequest = "detail_request";

    public static readonly IReadOnlyList<string> ConcernTypeLabels = new[]
    {
        ConcernTypeTopicUnknown,
        ConcernTypeComprehensionGap,
        ConcernTypeDetailRequest,
    };
}
