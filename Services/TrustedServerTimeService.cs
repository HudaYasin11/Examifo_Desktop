namespace Examifo_Desktop.Services;

public interface ITrustedTimeStore
{
    long? GetOffsetTicks();
    void SetOffsetTicks(long offsetTicks);
}

public sealed class TrustedServerTimeService(ITrustedTimeStore store, TimeProvider timeProvider)
{
    private static readonly TimeSpan MaximumRoundTrip = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private TimeSpan? _offset = LoadOffset(store);

    public DateTimeOffset UtcNow
    {
        get { lock (_gate) return timeProvider.GetUtcNow() + (_offset ?? TimeSpan.Zero); }
    }

    public bool HasTrustedOffset { get { lock (_gate) return _offset.HasValue; } }

    public void RecordSample(DateTimeOffset serverTimeUtc, DateTimeOffset requestStartedUtc,
        DateTimeOffset responseReceivedUtc)
    {
        if (serverTimeUtc == default || responseReceivedUtc < requestStartedUtc
            || responseReceivedUtc - requestStartedUtc > MaximumRoundTrip)
            return;
        DateTimeOffset midpoint = requestStartedUtc + TimeSpan.FromTicks(
            (responseReceivedUtc - requestStartedUtc).Ticks / 2);
        TimeSpan candidate = serverTimeUtc.ToUniversalTime() - midpoint.ToUniversalTime();
        lock (_gate)
        {
            _offset = candidate;
            store.SetOffsetTicks(candidate.Ticks);
        }
    }

    public DateTimeOffset CalculateDeadline(DateTimeOffset startedAtUtc, int? durationSeconds,
        DateTimeOffset mustSubmitBeforeUtc)
    {
        DateTimeOffset durationDeadline = durationSeconds is > 0
            ? startedAtUtc.AddSeconds(durationSeconds.Value) : mustSubmitBeforeUtc;
        return durationDeadline < mustSubmitBeforeUtc ? durationDeadline : mustSubmitBeforeUtc;
    }

    private static TimeSpan? LoadOffset(ITrustedTimeStore store)
    {
        long? ticks = store.GetOffsetTicks();
        return ticks is null || ticks < -TimeSpan.TicksPerDay || ticks > TimeSpan.TicksPerDay
            ? null : TimeSpan.FromTicks(ticks.Value);
    }
}
