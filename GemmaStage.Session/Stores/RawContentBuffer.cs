using System.Threading;

namespace GemmaStage.Session.Stores;

public enum RawContentKind
{
    Audio = 0,
    Image = 1,
    // Out-of-band text appended by the runtime to delimit On-Live and Final Q&A
    // rounds (the chosen concern's question text on round-open, the closing
    // sentinel on round-close). The Q&A flows append these inline so the next
    // cognitive cycle's IdeaReflector input shows the round between the
    // surrounding audio.
    QuestionMarker = 2,
    QaClosedMarker = 3,
}

public sealed record RawContentSegment(
    long Sequence,
    DateTimeOffset Timestamp,
    RawContentKind Kind,
    string Text);

// Chronological per-cycle buffer of audio transcripts and image examinations.
// Drain clears it so the next cycle starts empty.
public sealed class RawContentBuffer
{
    private readonly object _gate = new();
    private readonly List<RawContentSegment> _entries = new();
    private long _nextSequence;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public RawContentSegment AppendAudio(string transcript, DateTimeOffset timestamp)
    {
        Guard.NotNullOrWhiteSpace(transcript);
        return Append(RawContentKind.Audio, transcript, timestamp);
    }

    public RawContentSegment AppendImage(string examination, DateTimeOffset timestamp)
    {
        Guard.NotNullOrWhiteSpace(examination);
        return Append(RawContentKind.Image, examination, timestamp);
    }

    public RawContentSegment AppendQuestionMarker(string questionText, DateTimeOffset timestamp)
    {
        Guard.NotNullOrWhiteSpace(questionText);
        return Append(RawContentKind.QuestionMarker, questionText, timestamp);
    }

    public RawContentSegment AppendQaClosedMarker(string closingText, DateTimeOffset timestamp)
    {
        Guard.NotNullOrWhiteSpace(closingText);
        return Append(RawContentKind.QaClosedMarker, closingText, timestamp);
    }

    public IReadOnlyList<RawContentSegment> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    // Atomic snapshot + clear.
    public IReadOnlyList<RawContentSegment> Drain()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return Array.Empty<RawContentSegment>();
            }

            var snapshot = _entries.ToArray();
            _entries.Clear();
            return snapshot;
        }
    }

    private RawContentSegment Append(RawContentKind kind, string text, DateTimeOffset timestamp)
    {
        var entry = new RawContentSegment(
            Interlocked.Increment(ref _nextSequence),
            timestamp,
            kind,
            text);

        lock (_gate)
        {
            _entries.Add(entry);
        }

        return entry;
    }
}
