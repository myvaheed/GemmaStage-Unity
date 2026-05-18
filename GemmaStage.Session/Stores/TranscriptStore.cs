using System;
using System.Collections.Generic;

namespace GemmaStage.Session.Stores;

public sealed record TranscriptEntry(long Sequence, DateTimeOffset Timestamp, string Text);

public sealed class TranscriptStore
{
    private readonly object _gate = new();
    private readonly List<TranscriptEntry> _entries = new();

    public void Append(TranscriptEntry entry)
    {
        Guard.NotNull(entry);

        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<TranscriptEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
