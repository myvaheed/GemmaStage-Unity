using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GemmaStage.Session.Stores;

public enum InputKind
{
    Audio = 0,
    Image = 1,
}

public sealed record InputEnvelope(
    long Sequence,
    DateTimeOffset Timestamp,
    InputKind Kind,
    ReadOnlyMemory<byte> Payload,
    TimeSpan? InputDuration = null);

public sealed class InputQueue
{
    private readonly ConcurrentQueue<InputEnvelope> _queue = new();
    private long _nextSequence;

    public int Count => _queue.Count;

    public InputEnvelope EnqueueAudio(byte[] wav, TimeSpan? inputDuration = null)
    {
        Guard.NotNull(wav);
        return Enqueue(InputKind.Audio, wav, inputDuration);
    }

    public InputEnvelope EnqueueImage(byte[] png)
    {
        Guard.NotNull(png);
        return Enqueue(InputKind.Image, png, inputDuration: null);
    }

    public bool TryDequeue(out InputEnvelope? entry)
    {
        if (_queue.TryDequeue(out var item))
        {
            entry = Clone(item);
            return true;
        }

        entry = null;
        return false;
    }

    public IReadOnlyList<InputEnvelope> Snapshot()
    {
        return _queue
            .ToArray()
            .Select(Clone)
            .ToArray();
    }

    private InputEnvelope Enqueue(InputKind kind, byte[] payload, TimeSpan? inputDuration)
    {
        if (inputDuration is { } duration && duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputDuration),
                inputDuration,
                "Input duration cannot be negative.");
        }

        var copy = payload.ToArray();
        var entry = new InputEnvelope(
            Interlocked.Increment(ref _nextSequence),
            DateTimeOffset.UtcNow,
            kind,
            copy,
            inputDuration);
        _queue.Enqueue(entry);
        return Clone(entry);
    }

    private static InputEnvelope Clone(InputEnvelope entry)
    {
        return new InputEnvelope(
            entry.Sequence,
            entry.Timestamp,
            entry.Kind,
            entry.Payload.ToArray(),
            entry.InputDuration);
    }
}
