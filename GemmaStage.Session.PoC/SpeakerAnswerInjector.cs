using GemmaStage.Session.Stores;

namespace GemmaStage.Session.PoC;

// PoC-only. Injects a speaker-produced answer text into the session as if ASR
// had transcribed real spoken audio. The session normally adds these entries
// inside CognitiveCoordinator.SendAudioAndPersist, but for the PoC Q&A flow
// the speaker text is already in hand (it came from SpeakerConversation, not
// from an audio chunk through Asr) so we bypass Asr and write directly to the
// public stores.
//
// Sequence numbers start above 1_000_000 to avoid colliding with ASR turn
// sequences (which begin at 1 and grow with every chunk).
internal sealed class SpeakerAnswerInjector
{
    private long _nextSequence = 1_000_000L;

    public void Inject(Session session, string answerText)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrWhiteSpace(answerText)) return;

        var seq = System.Threading.Interlocked.Increment(ref _nextSequence);
        var ts = DateTimeOffset.UtcNow;
        var text = answerText.Trim();

        session.TranscriptStore.Append(new TranscriptEntry(seq, ts, text));
        session.MetricsStore.Append(new MetricEntry(
            Sequence: seq,
            Timestamp: ts,
            Clarity: "normal",
            Emotion: "calm",
            Grammar: "good",
            ChunkCompleted: true));
        session.RawContentBuffer.AppendAudio(text, ts);
    }
}
