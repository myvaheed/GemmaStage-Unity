using System;
using System.Collections.Generic;

namespace GemmaStage.Session.Stores;

public sealed record ImageEntry(long Sequence, DateTimeOffset Timestamp, string Examination);

public sealed class ImageStore
{
    private readonly object _gate = new();
    private readonly List<ImageEntry> _entries = new();

    public void Append(ImageEntry entry)
    {
        Guard.NotNull(entry);

        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<ImageEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
