using GemmaStage.Session.Inquirer;

namespace GemmaStage.Session.Stores;

public sealed record ConcernEntry(
    long Id,
    string Question,
    DateTimeOffset UpdatedAt);

public sealed class ConcernStore
{
    private readonly object _gate = new();
    private readonly List<ConcernEntry> _entries = new();

    public void ReplaceOpen(IEnumerable<InquirerConcern> concerns, DateTimeOffset? updatedAt = null)
    {
        Guard.NotNull(concerns);

        var timestamp = updatedAt ?? DateTimeOffset.UtcNow;
        var next = concerns
            .Select(concern =>
            {
                Guard.NotNullOrWhiteSpace(concern.Question);
                return new ConcernEntry(concern.Id, concern.Question, timestamp);
            })
            .ToList();

        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(next);
        }
    }

    public IReadOnlyList<ConcernEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public IReadOnlyList<ConcernEntry> OpenSnapshot()
    {
        return Snapshot();
    }
}
