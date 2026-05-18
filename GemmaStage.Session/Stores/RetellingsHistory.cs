using GemmaStage.Session.IdeaReflector;
using GemmaStage.Session.Inquirer;

namespace GemmaStage.Session.Stores;

// One entry per cognitive cycle, capturing the per-cycle raw content buffer
// plus the IdeaReflector retelling + main-idea output and the Inquirer concern delta.
// The retellings history is unbounded - Inquirer reads it whole each cycle.
public sealed record RetellingHistoryEntry(
    int CycleIndex,
    DateTimeOffset Timestamp,
    string Retelling,
    IReadOnlyList<RawContentSegment> RawContent,
    IdeaReflectorReflection? IdeaReflectorOutput,
    InquirerReflection Reflection,
    InquirerStateSnapshot StateAfter);

public sealed class RetellingsHistory
{
    private readonly object _gate = new();
    private readonly List<RetellingHistoryEntry> _entries = new();

    public void Append(RetellingHistoryEntry entry)
    {
        Guard.NotNull(entry);

        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<RetellingHistoryEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
