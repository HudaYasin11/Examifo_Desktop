namespace Examifo_Desktop.Services;

public sealed class MauiPreferencesTrustedTimeStore : ITrustedTimeStore
{
    private const string Key = "examifo.server_time_offset_ticks.v1";
    public long? GetOffsetTicks() => Preferences.Default.ContainsKey(Key) ? Preferences.Default.Get(Key, 0L) : null;
    public void SetOffsetTicks(long offsetTicks) => Preferences.Default.Set(Key, offsetTicks);
}
