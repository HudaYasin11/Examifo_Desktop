using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Services;

public interface ISecureValueStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    void Remove(string key);
}

public sealed record AuthSession(
    int SchemaVersion,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc,
    Guid DeviceId,
    DateTimeOffset? ServerTimeUtc,
    Guid? UserId,
    string? UserName,
    string? UserEmail)
{
    public const int CurrentSchemaVersion = 1;

    public static AuthSession FromLoginResponse(LoginResponse response) => new(
        CurrentSchemaVersion,
        response.AccessToken,
        response.AccessTokenExpiresAtUtc,
        response.RefreshToken,
        response.RefreshTokenExpiresAtUtc,
        response.DeviceId,
        response.ServerTimeUtc,
        response.User.Id,
        response.User.Name,
        response.User.Email);
}

public sealed class AuthSessionStore(ISecureValueStore secureStore)
{
    public const string SessionKey = "examifo.auth_session.v1";
    private static readonly string[] LegacyKeys =
    [
        "examifo.access_token", "examifo.access_token_expiry", "examifo.refresh_token",
        "examifo.refresh_token_expiry", "examifo.device_id"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AuthSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? serialized = await secureStore.GetAsync(SessionKey);
            if (!string.IsNullOrWhiteSpace(serialized))
            {
                AuthSession? session = TryDeserialize(serialized);
                if (session is not null)
                    return session;

                secureStore.Remove(SessionKey);
            }

            return await TryMigrateLegacySessionAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AuthSession session, CancellationToken cancellationToken = default)
    {
        if (!IsValid(session))
            throw new InvalidOperationException("Refusing to store an invalid Examifo authentication session.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await secureStore.SetAsync(SessionKey, JsonSerializer.Serialize(session, JsonOptions));
            RemoveLegacyKeys();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            secureStore.Remove(SessionKey);
            RemoveLegacyKeys();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AuthSession?> TryMigrateLegacySessionAsync()
    {
        string? accessToken = await secureStore.GetAsync(LegacyKeys[0]);
        string? accessExpiryValue = await secureStore.GetAsync(LegacyKeys[1]);
        string? refreshToken = await secureStore.GetAsync(LegacyKeys[2]);
        string? refreshExpiryValue = await secureStore.GetAsync(LegacyKeys[3]);
        string? deviceIdValue = await secureStore.GetAsync(LegacyKeys[4]);

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken)
            || !DateTimeOffset.TryParse(accessExpiryValue, out DateTimeOffset accessExpiry)
            || !DateTimeOffset.TryParse(refreshExpiryValue, out DateTimeOffset refreshExpiry)
            || !Guid.TryParse(deviceIdValue, out Guid deviceId) || deviceId == Guid.Empty)
        {
            RemoveLegacyKeys();
            return null;
        }

        var migrated = new AuthSession(AuthSession.CurrentSchemaVersion, accessToken, accessExpiry,
            refreshToken, refreshExpiry, deviceId, null, null, null, null);
        await secureStore.SetAsync(SessionKey, JsonSerializer.Serialize(migrated, JsonOptions));
        RemoveLegacyKeys();
        return migrated;
    }

    private static AuthSession? TryDeserialize(string serialized)
    {
        try
        {
            AuthSession? session = JsonSerializer.Deserialize<AuthSession>(serialized, JsonOptions);
            return session is not null && IsValid(session) ? session : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValid(AuthSession session) =>
        session.SchemaVersion == AuthSession.CurrentSchemaVersion
        && !string.IsNullOrWhiteSpace(session.AccessToken)
        && !string.IsNullOrWhiteSpace(session.RefreshToken)
        && session.DeviceId != Guid.Empty
        && session.AccessTokenExpiresAtUtc != default
        && session.RefreshTokenExpiresAtUtc != default;

    private void RemoveLegacyKeys()
    {
        foreach (string key in LegacyKeys)
            secureStore.Remove(key);
    }
}
