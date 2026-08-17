namespace Examifo_Desktop.Services;

public readonly record struct AttemptClockSample(
    DateTimeOffset EffectiveUtcNow,
    TimeSpan Remaining,
    bool ClockChangeDetected,
    TimeSpan WallClockDrift);

public sealed class AttemptClock
{
    private static readonly TimeSpan DriftThreshold = TimeSpan.FromSeconds(5);
    private readonly TrustedServerTimeService _trustedTime;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _deadlineUtc;
    private readonly DateTimeOffset _baselineUtc;
    private readonly long _baselineTimestamp;
    private DateTimeOffset _floorUtc;
    private bool _driftActive;

    public AttemptClock(DateTime deadlineUtc, DateTime lastObservedUtc,
        TrustedServerTimeService trustedTime, TimeProvider? timeProvider = null)
    {
        _trustedTime = trustedTime;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deadlineUtc = AsUtc(deadlineUtc);
        _baselineUtc = trustedTime.UtcNow;
        _baselineTimestamp = _timeProvider.GetTimestamp();
        _floorUtc = lastObservedUtc == default ? _baselineUtc : AsUtc(lastObservedUtc);
    }

    public AttemptClockSample Sample()
    {
        DateTimeOffset monotonicUtc = _baselineUtc
            + _timeProvider.GetElapsedTime(_baselineTimestamp, _timeProvider.GetTimestamp());
        DateTimeOffset wallUtc = _trustedTime.UtcNow;
        TimeSpan drift = wallUtc - monotonicUtc;
        bool suspicious = drift.Duration() >= DriftThreshold;
        bool newlyDetected = suspicious && !_driftActive;
        _driftActive = suspicious;

        // Once an attempt is running, its countdown is driven exclusively by elapsed
        // monotonic time. The wall clock is observed only to detect tampering; allowing it
        // into effectiveUtc would let either a manual clock jump or a refreshed server-time
        // offset expire the attempt incorrectly.
        DateTimeOffset effectiveUtc = Max(_floorUtc, monotonicUtc);
        _floorUtc = effectiveUtc;
        TimeSpan remaining = _deadlineUtc - effectiveUtc;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return new(effectiveUtc, remaining, newlyDetected, drift);
    }

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;
}
