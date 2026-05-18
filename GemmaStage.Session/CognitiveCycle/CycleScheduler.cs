namespace GemmaStage.Session.CognitiveCycle;

public sealed class CycleScheduler
{
    public static readonly TimeSpan DefaultMinElapsed = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _minElapsed;
    private readonly TimeProvider _time;
    private DateTimeOffset _lastCycleAt;
    private TimeSpan _elapsedAudioSinceCycle;
    private TimeSpan _totalAudio;
    private bool _usesAudioTimeline;

    public CycleScheduler(TimeSpan? minElapsed = null, TimeProvider? time = null)
    {
        _minElapsed = minElapsed ?? DefaultMinElapsed;
        if (_minElapsed < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minElapsed),
                _minElapsed,
                "Minimum elapsed time cannot be negative.");
        }

        _time = time ?? TimeProvider.System;
        _lastCycleAt = _time.GetUtcNow();
    }

    public TimeSpan MinElapsed => _minElapsed;

    public DateTimeOffset LastCycleAt => _lastCycleAt;

    public TimeSpan ElapsedAudioSinceCycle => _elapsedAudioSinceCycle;

    public TimeSpan TotalAudio => _totalAudio;

    public bool UsesAudioTimeline => _usesAudioTimeline;

    public void AdvanceAudio(TimeSpan inputDuration)
    {
        if (inputDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputDuration),
                inputDuration,
                "Input duration cannot be negative.");
        }

        _usesAudioTimeline = true;
        _elapsedAudioSinceCycle += inputDuration;
        _totalAudio += inputDuration;
    }

    // Audio is the only cycle trigger; both the elapsed-time gate and the
    // chunk_completed boundary must be open.
    public bool ShouldTriggerAfterAudio(bool chunkCompleted)
    {
        if (!chunkCompleted)
        {
            return false;
        }

        return IsElapsedGateOpen();
    }

    public void RecordCycle(DateTimeOffset? at = null)
    {
        _lastCycleAt = at ?? _time.GetUtcNow();
        _elapsedAudioSinceCycle = TimeSpan.Zero;
    }

    private bool IsElapsedGateOpen()
    {
        if (_usesAudioTimeline)
        {
            return _elapsedAudioSinceCycle >= _minElapsed;
        }

        return _time.GetUtcNow() - _lastCycleAt >= _minElapsed;
    }
}
