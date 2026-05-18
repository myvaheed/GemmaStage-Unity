using GemmaStage.Session.Inquirer;

namespace GemmaStage.Session.Stores;

public sealed record ArchivedConcernEntry(
    long Id,
    string Question,
    InquirerConcernType Type,
    DateTimeOffset ArchivedAt,
    string Reason);

public sealed class ConcernArchive
{
    public const string OverflowReason = "overflow";
    public const string SessionEndReason = "session_end";

    private readonly object _gate = new();
    private readonly List<ArchivedConcernEntry> _entries = new();

    public void Append(IEnumerable<ArchivedConcernEntry> entries)
    {
        Guard.NotNull(entries);

        lock (_gate)
        {
            _entries.AddRange(entries);
        }
    }

    public void AppendOverflow(IEnumerable<ArchivedConcernEntry> entries)
    {
        Append(entries);
    }

    public void AppendSessionEnd(IEnumerable<ArchivedConcernEntry> entries)
    {
        Append(entries);
    }

    public IReadOnlyList<ArchivedConcernEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
