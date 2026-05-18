using System;
using System.Collections.Generic;

namespace GemmaStage.Session.Stores;

public sealed record MetricEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string? Clarity,
    string? Emotion,
    string? Grammar,
    bool ChunkCompleted,
    double? TimeToFirstTokenSeconds = null,
    double? DecodeTokensPerSecond = null,
    int? DecodeTokenCount = null);

public sealed class MetricsStore
{
    private readonly object _gate = new();
    private readonly List<MetricEntry> _entries = new();

    public void Append(MetricEntry entry)
    {
        Guard.NotNull(entry);

        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<MetricEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}
