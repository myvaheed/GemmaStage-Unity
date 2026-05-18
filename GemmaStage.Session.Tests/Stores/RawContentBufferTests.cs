using GemmaStage.Session.Stores;

namespace GemmaStage.Session.Tests.Stores;

internal static class RawContentBufferTests
{
    public static void Run()
    {
        var buffer = new RawContentBuffer();
        var t0 = DateTimeOffset.Parse("2026-04-30T10:00:00+00:00");

        buffer.AppendAudio("hello world", t0);
        buffer.AppendImage("slide showing diagram", t0.AddSeconds(3));
        buffer.AppendAudio("continuing speech", t0.AddSeconds(5));

        var snapshot = buffer.Snapshot();
        AssertEx.Equal(3, snapshot.Count, "Snapshot should preserve every appended segment");
        AssertEx.Equal(RawContentKind.Audio, snapshot[0].Kind, "First segment should be audio");
        AssertEx.Equal(RawContentKind.Image, snapshot[1].Kind, "Image arrival should keep its slot in chronological order");
        AssertEx.Equal(RawContentKind.Audio, snapshot[2].Kind, "Audio resuming after the image should sit at the end");
        AssertEx.Equal("slide showing diagram", snapshot[1].Text, "Image text should round-trip");

        var drained = buffer.Drain();
        AssertEx.Equal(3, drained.Count, "Drain should return every segment that was in the buffer");
        AssertEx.Equal(0, buffer.Count, "Drain must clear the buffer atomically");

        var emptyDrain = buffer.Drain();
        AssertEx.Equal(0, emptyDrain.Count, "Draining an empty buffer should yield no segments");

        // Subsequent appends after a drain must not lose ordering or sequencing.
        buffer.AppendAudio("post-drain audio", t0.AddSeconds(70));
        var afterDrainSnapshot = buffer.Snapshot();
        AssertEx.Equal(1, afterDrainSnapshot.Count, "New appends after Drain start a fresh per-cycle buffer");
        AssertEx.True(
            afterDrainSnapshot[0].Sequence > snapshot[2].Sequence,
            "Sequence numbers should keep advancing across drains");
    }

    public static void RunMarkers()
    {
        var buffer = new RawContentBuffer();
        var t0 = DateTimeOffset.Parse("2026-04-30T10:00:00+00:00");

        buffer.AppendAudio("speaker context", t0);
        buffer.AppendQuestionMarker("Why does the audience ask this?", t0.AddSeconds(2));
        buffer.AppendAudio("user's answer audio", t0.AddSeconds(5));
        buffer.AppendQaClosedMarker("Hope I answered your question", t0.AddSeconds(10));

        var snapshot = buffer.Snapshot();
        AssertEx.Equal(4, snapshot.Count, "Markers should occupy distinct slots in the chronological buffer");
        AssertEx.Equal(RawContentKind.Audio, snapshot[0].Kind, "Pre-question audio comes first");
        AssertEx.Equal(RawContentKind.QuestionMarker, snapshot[1].Kind, "Question marker delimits the round open");
        AssertEx.Equal(RawContentKind.Audio, snapshot[2].Kind, "Answer audio sits between the markers");
        AssertEx.Equal(RawContentKind.QaClosedMarker, snapshot[3].Kind, "Closing marker delimits the round close");
        AssertEx.Equal("Why does the audience ask this?", snapshot[1].Text, "Question marker text should round-trip");
        AssertEx.Equal("Hope I answered your question", snapshot[3].Text, "Closing marker text should round-trip");
    }
}
