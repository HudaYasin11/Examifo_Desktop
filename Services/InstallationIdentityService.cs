namespace Examifo_Desktop.Services;

public interface IInstallationIdentityStore
{
    string? Get(string key);
    void Set(string key, string value);
}

/// <summary>
/// Owns the stable identifier for this application installation. This identifier is
/// generated locally and is deliberately separate from the backend-issued device ID.
/// </summary>
public sealed class InstallationIdentityService(IInstallationIdentityStore store)
{
    public const string InstallationIdKey = "examifo.installation_id";
    private readonly object _gate = new();
    private Guid? _cachedInstallationId;

    public Guid GetOrCreateInstallationId()
    {
        lock (_gate)
        {
            if (_cachedInstallationId is { } cached)
                return cached;

            string? savedValue = store.Get(InstallationIdKey);
            if (Guid.TryParseExact(savedValue, "D", out Guid savedId) && savedId != Guid.Empty)
            {
                _cachedInstallationId = savedId;
                return savedId;
            }

            Guid installationId = Guid.NewGuid();
            store.Set(InstallationIdKey, installationId.ToString("D"));
            _cachedInstallationId = installationId;
            return installationId;
        }
    }
}
