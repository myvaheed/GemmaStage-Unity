using GemmaStage.Session.Inquirer;
using GemmaStage.Session.Stores;

namespace GemmaStage.Session.Tests.Stores;

internal static class QaRoundsHistoryTests
{
    public static void Run()
    {
        var history = new QaRoundsHistory();
        var t0 = DateTimeOffset.Parse("2026-04-30T10:00:00+00:00");

        var live = new InquirerConcern(101, "Why caching?", InquirerConcernType.ComprehensionGap);
        var liveHandle = history.BeginRound(QaPhase.Live, live, t0);
        AssertEx.Equal(0, history.Snapshot().Count, "An open round must not yet appear in the snapshot");

        history.CloseRound(liveHandle, t0.AddSeconds(20));

        var afterLive = history.Snapshot();
        AssertEx.Equal(1, afterLive.Count, "Closing a round should add a single entry to the snapshot");
        AssertEx.Equal(QaPhase.Live, afterLive[0].Phase, "Live-phase round should be tagged as Live");
        AssertEx.Equal(101L, afterLive[0].ConcernId, "Concern id should round-trip into the closed entry");
        AssertEx.Equal("Why caching?", afterLive[0].QuestionText, "Question text should round-trip into the closed entry");
        AssertEx.True(afterLive[0].ClosedAt > afterLive[0].OpenedAt, "ClosedAt must be after OpenedAt");

        var final = new InquirerConcern(202, "What about invalidation?", InquirerConcernType.ComprehensionGap);
        var finalHandle = history.BeginRound(QaPhase.Final, final, t0.AddMinutes(5));
        history.CloseRound(finalHandle, t0.AddMinutes(5).AddSeconds(45));

        var afterFinal = history.Snapshot();
        AssertEx.Equal(2, afterFinal.Count, "Each closed round should append a separate entry");
        AssertEx.Equal(QaPhase.Final, afterFinal[1].Phase, "Final-phase round should be tagged as Final");
        AssertEx.Equal(202L, afterFinal[1].ConcernId, "Final round concern id should round-trip");
    }
}
