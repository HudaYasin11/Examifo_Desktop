namespace Examifo_Desktop.Infrastructure.Sync;

public static class ResultPollingPolicy
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)
    ];

    public static TimeSpan GetDelay(int completedPolls)
    {
        if (completedPolls < 0) throw new ArgumentOutOfRangeException(nameof(completedPolls));
        return Delays[Math.Min(completedPolls, Delays.Length - 1)];
    }
}
