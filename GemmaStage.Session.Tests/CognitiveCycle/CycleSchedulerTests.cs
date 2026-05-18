using GemmaStage.Session.CognitiveCycle;

namespace GemmaStage.Session.Tests.CognitiveCycle;

internal sealed class StubTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public StubTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta)
    {
        _now = _now.Add(delta);
    }
}

internal static class CycleSchedulerTests
{
    public static void Run()
    {
        var start = DateTimeOffset.Parse("2026-04-23T10:00:00+00:00");
        var time = new StubTimeProvider(start);
        var scheduler = new CycleScheduler(TimeSpan.FromSeconds(60), time);

        AssertEx.True(!scheduler.ShouldTriggerAfterAudio(true), "At t=0 the elapsed gate is not yet open");

        time.Advance(TimeSpan.FromSeconds(59));
        AssertEx.True(!scheduler.ShouldTriggerAfterAudio(true), "59s is below the 60s minimum");

        time.Advance(TimeSpan.FromSeconds(2));
        AssertEx.True(!scheduler.ShouldTriggerAfterAudio(chunkCompleted: false), "Even past the gate, incomplete chunk blocks trigger");
        AssertEx.True(scheduler.ShouldTriggerAfterAudio(chunkCompleted: true), "Past gate + chunk_completed should fire");

        scheduler.RecordCycle(time.GetUtcNow());
        AssertEx.True(!scheduler.ShouldTriggerAfterAudio(true), "RecordCycle must reset the elapsed clock");

        time.Advance(TimeSpan.FromSeconds(61));
        AssertEx.True(scheduler.ShouldTriggerAfterAudio(true), "Gate should open again 60s after the recorded cycle");
    }

    public static void RunAudioTimeline()
    {
        var start = DateTimeOffset.Parse("2026-04-23T10:00:00+00:00");
        var time = new StubTimeProvider(start);
        var scheduler = new CycleScheduler(TimeSpan.FromSeconds(60), time);

        time.Advance(TimeSpan.FromMinutes(10));
        scheduler.AdvanceAudio(TimeSpan.FromSeconds(59));
        AssertEx.True(scheduler.UsesAudioTimeline, "Explicit input duration should switch scheduler to audio timeline");
        AssertEx.True(!scheduler.ShouldTriggerAfterAudio(true), "Wall-clock time must not open the gate once audio timeline is active");

        scheduler.AdvanceAudio(TimeSpan.FromSeconds(1));
        AssertEx.True(!scheduler.ShouldTriggerAfterAudio(false), "Audio timeline still respects chunk_completed");
        AssertEx.True(scheduler.ShouldTriggerAfterAudio(true), "Audio timeline opens at the configured input duration");

        scheduler.RecordCycle();
        AssertEx.Equal(TimeSpan.Zero, scheduler.ElapsedAudioSinceCycle, "RecordCycle must reset audio elapsed time");
        AssertEx.True(!scheduler.ShouldTriggerAfterAudio(true), "After reset, audio time must accumulate again");
    }
}

internal static class SessionConfigTranscriptSummarizerDefaultTests
{
    public static void Run()
    {
        var cfg = new GemmaStage.Session.SessionConfig("model.gguf");
        AssertEx.Equal(500, cfg.TranscriptSummarizerTokenBudget, "TranscriptSummarizer token budget should default to 500 (was 1000)");
    }
}
