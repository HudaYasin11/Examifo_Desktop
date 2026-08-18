namespace Examifo_Desktop.Infrastructure.Sync;

public static class SyncRetryPolicy
{
    private const int MaximumExponent = 6;
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);

    public static TimeSpan GetDelay(int completedRetryCount, double jitterSample)
    {
        if (completedRetryCount < 0) throw new ArgumentOutOfRangeException(nameof(completedRetryCount));
        if (jitterSample is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(jitterSample));
        int exponent = Math.Min(completedRetryCount, MaximumExponent);
        double baseSeconds = Math.Pow(2, exponent + 1);
        double jitterMultiplier = 0.75 + (jitterSample * 0.5);
        return TimeSpan.FromSeconds(Math.Min(baseSeconds * jitterMultiplier, MaximumDelay.TotalSeconds));
    }
}
