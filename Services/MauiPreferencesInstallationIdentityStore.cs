namespace Examifo_Desktop.Services;

public sealed class MauiPreferencesInstallationIdentityStore : IInstallationIdentityStore
{
    public string? Get(string key) => Preferences.Default.Get<string?>(key, null);

    public void Set(string key, string value) => Preferences.Default.Set(key, value);
}
