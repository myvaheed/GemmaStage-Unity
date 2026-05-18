using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GemmaStage.Session.PoC;

// Writes one JSON object per line to a file. Thread-safe via lock.
internal sealed class JsonlWriter : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly Stopwatch _clock;
    private bool _disposed;

    public JsonlWriter(string path, Stopwatch clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public double MonotonicMs => _clock.Elapsed.TotalMilliseconds;

    public void Write<T>(T entry)
    {
        var json = JsonSerializer.Serialize(entry, SerializerOptions);
        lock (_gate)
        {
            _writer.WriteLine(json);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _writer.Dispose();
        _disposed = true;
    }
}
