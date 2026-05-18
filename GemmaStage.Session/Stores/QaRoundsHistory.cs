using GemmaStage.Session.Inquirer;

namespace GemmaStage.Session.Stores;

public enum QaPhase
{
    Live = 0,
    Final = 1,
}

// Records one closed Q&A round (live or final). Carries the originating
// concern ID so post-performance roles can decide whether the answer
// resolved that concern by joining against the inquirer reflections.
public sealed record QaRoundEntry(
    QaPhase Phase,
    long ConcernId,
    string QuestionText,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt);

// Returned from BeginRound and consumed by CloseRound. Carries the
// open-round identity across the Begin -> Close window so the store
// itself never holds in-progress state.
public sealed class QaRoundHandle
{
    internal QaPhase Phase { get; }
    internal long ConcernId { get; }
    internal string QuestionText { get; }
    internal DateTimeOffset OpenedAt { get; }

    internal QaRoundHandle(QaPhase phase, long concernId, string questionText, DateTimeOffset openedAt)
    {
        Phase = phase;
        ConcernId = concernId;
        QuestionText = questionText;
        OpenedAt = openedAt;
    }
}

public sealed class QaRoundsHistory
{
    private readonly object _gate = new();
    private readonly List<QaRoundEntry> _entries = new();

    public QaRoundHandle BeginRound(QaPhase phase, InquirerConcern concern, DateTimeOffset openedAt)
    {
        Guard.NotNull(concern);
        Guard.NotNullOrWhiteSpace(concern.Question);
        return new QaRoundHandle(phase, concern.Id, concern.Question.Trim(), openedAt);
    }

    public QaRoundEntry CloseRound(QaRoundHandle handle, DateTimeOffset closedAt)
    {
        Guard.NotNull(handle);
        var entry = new QaRoundEntry(
            handle.Phase,
            handle.ConcernId,
            handle.QuestionText,
            handle.OpenedAt,
            closedAt);

        lock (_gate)
        {
            _entries.Add(entry);
        }
        return entry;
    }

    public IReadOnlyList<QaRoundEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
