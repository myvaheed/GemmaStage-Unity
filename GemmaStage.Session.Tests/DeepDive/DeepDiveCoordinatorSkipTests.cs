using System;
using GemmaStage.Session.DeepDive;
using QaHandlingNS = GemmaStage.Session.DeepDive.QaHandling;

namespace GemmaStage.Session.Tests.DeepDive;

internal static class DeepDiveCoordinatorSkipTests
{
    public static void Run()
    {
        QaSkipsWhenNoSpans();
        QaRunsWhenAtLeastOneSpan();
    }

    private static void QaSkipsWhenNoSpans()
    {
        var input = new QaHandlingNS.QaHandlingInput(Array.Empty<QaSpan>());
        AssertEx.True(DeepDiveCoordinator.ShouldSkipQa(input), "Zero Q&A rounds means skip");
    }

    private static void QaRunsWhenAtLeastOneSpan()
    {
        var input = new QaHandlingNS.QaHandlingInput(new[]
        {
            new QaSpan(GemmaStage.Session.Stores.QaPhase.Live, "q", "a", QaResolution.Resolved),
        });
        AssertEx.True(!DeepDiveCoordinator.ShouldSkipQa(input), "Any closed round must run the model");
    }
}
