using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Services;

public sealed class AuthenticationService(AuthApiClient authApiClient)
{
    private const string InstallationIdKey = "examifo.installation_id";
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return LoginResult.Failed("Enter your email and password.");

        try
        {
            LoginResponse response = await authApiClient.LoginAsync(new LoginRequest(email, password,
                new DeviceInput(GetOrCreateInstallationId(), DeviceInfo.Name, DeviceInfo.Platform.ToString(),
                    AppInfo.Current.VersionString, null)), cancellationToken);
            await StoreSessionAsync(response);
            return LoginResult.Succeeded(response.User.Name);
        }
        catch (AuthApiException ex) when (ex.Code is "INVALID_CREDENTIALS")
        {
            return LoginResult.Failed("Invalid email or password.");
        }
        catch (AuthApiException ex) when (ex.Code is "ACCOUNT_NOT_CONFIRMED")
        {
            return LoginResult.Failed("Confirm your email address before signing in.");
        }
        catch (AuthApiException ex)
        {
            return LoginResult.Failed(ex.Message);
        }
        catch (HttpRequestException)
        {
            return LoginResult.Failed("Cannot reach Examifo. Check your internet connection and try again.");
        }
        catch (TaskCanceledException)
        {
            return LoginResult.Failed("The login request timed out. Please try again.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Login failed: {ex}");
            return LoginResult.Failed("Unable to save your Examifo session on this device.");
        }
    }

    public Task<string?> GetAccessTokenAsync() =>
        SecureStorage.Default.GetAsync("examifo.access_token");

    public async Task<Guid> GetDeviceIdAsync()
    {
        string value = await SecureStorage.Default.GetAsync("examifo.device_id")
            ?? throw new AuthApiException(System.Net.HttpStatusCode.Unauthorized, "LOGIN_REQUIRED", "Sign in again.");
        return Guid.Parse(value);
    }

    public async Task<string> GetValidAccessTokenAsync(
        CancellationToken cancellationToken = default)
    {
        string? token = await GetAccessTokenAsync();
        string? expiryValue = await SecureStorage.Default.GetAsync("examifo.access_token_expiry");
        bool nearExpiry = !DateTimeOffset.TryParse(expiryValue, out DateTimeOffset expiry)
            || expiry <= DateTimeOffset.UtcNow.AddMinutes(1);

        if (!nearExpiry && !string.IsNullOrWhiteSpace(token))
            return token;

        return await RefreshSessionAsync(null, cancellationToken);
    }

    public Task<string> RefreshAfterUnauthorizedAsync(
        string rejectedAccessToken,
        CancellationToken cancellationToken = default) =>
        RefreshSessionAsync(rejectedAccessToken, cancellationToken);

    private async Task<string> RefreshSessionAsync(
        string? rejectedAccessToken,
        CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            string? currentToken = await GetAccessTokenAsync();
            string? expiryValue = await SecureStorage.Default.GetAsync("examifo.access_token_expiry");
            if (rejectedAccessToken is not null
                && !string.Equals(currentToken, rejectedAccessToken, StringComparison.Ordinal))
                return currentToken ?? throw new InvalidOperationException("The refreshed access token is unavailable.");

            if (rejectedAccessToken is null
                && DateTimeOffset.TryParse(expiryValue, out DateTimeOffset expiry)
                && expiry > DateTimeOffset.UtcNow.AddMinutes(1)
                && !string.IsNullOrWhiteSpace(currentToken))
                return currentToken;

            string refreshToken = await SecureStorage.Default.GetAsync("examifo.refresh_token")
                ?? throw new AuthApiException(System.Net.HttpStatusCode.Unauthorized, "LOGIN_REQUIRED", "Sign in again.");
            string deviceIdValue = await SecureStorage.Default.GetAsync("examifo.device_id")
                ?? throw new AuthApiException(System.Net.HttpStatusCode.Unauthorized, "LOGIN_REQUIRED", "Sign in again.");

            LoginResponse response = await authApiClient.RefreshAsync(
                new RefreshRequest(refreshToken, Guid.Parse(deviceIdValue)), cancellationToken);
            await StoreSessionAsync(response);
            return response.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static Guid GetOrCreateInstallationId()
    {
        string? saved = Preferences.Default.Get<string?>(InstallationIdKey, null);
        if (Guid.TryParse(saved, out Guid installationId)) return installationId;
        installationId = Guid.NewGuid();
        Preferences.Default.Set(InstallationIdKey, installationId.ToString());
        return installationId;
    }

    private static async Task StoreSessionAsync(LoginResponse response)
    {
        await SecureStorage.Default.SetAsync("examifo.access_token", response.AccessToken);
        await SecureStorage.Default.SetAsync("examifo.access_token_expiry", response.AccessTokenExpiresAtUtc.ToString("O"));
        await SecureStorage.Default.SetAsync("examifo.refresh_token", response.RefreshToken);
        await SecureStorage.Default.SetAsync("examifo.refresh_token_expiry", response.RefreshTokenExpiresAtUtc.ToString("O"));
        await SecureStorage.Default.SetAsync("examifo.device_id", response.DeviceId.ToString());
    }
}

public sealed record LoginResult(bool Success, string? UserName, string? ErrorMessage)
{
    public static LoginResult Succeeded(string userName) => new(true, userName, null);
    public static LoginResult Failed(string message) => new(false, null, message);
}
