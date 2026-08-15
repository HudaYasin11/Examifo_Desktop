using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Services;

public sealed class AuthenticationService(
    AuthApiClient authApiClient,
    InstallationIdentityService installationIdentityService,
    AuthSessionStore sessionStore,
    TokenRefreshCoordinator tokenRefreshCoordinator,
    TrustedServerTimeService trustedTime,
    SessionStateService sessionState,
    SessionLogoutService logoutService,
    DatabaseService databaseService)
{
    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return LoginResult.Failed("Enter your email and password.");

        sessionState.BeginSignIn();
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            Guid installationId = installationIdentityService.GetOrCreateInstallationId();
            LoginResponse response = await authApiClient.LoginAsync(new LoginRequest(email, password,
                new DeviceInput(installationId, DeviceInfo.Name, DeviceInfo.Platform.ToString(),
                    AppInfo.Current.VersionString, null)), cancellationToken);
            await databaseService.SaveLocalUserAsync(response.User.Id, response.User.Name,
                response.User.Email, DateTime.UtcNow, cancellationToken);
            await databaseService.SaveLocalDeviceAsync(new LocalDeviceRecord
            {
                DeviceId = response.DeviceId,
                InstallationId = installationId,
                EncryptedName = DeviceInfo.Name,
                Platform = DeviceInfo.Platform.ToString(),
                AppVersion = AppInfo.Current.VersionString,
                Status = "Active",
                UpdatedAtUtc = DateTime.UtcNow
            }, cancellationToken);
            await sessionStore.SaveAsync(AuthSession.FromLoginResponse(response), cancellationToken);
            trustedTime.RecordSample(response.ServerTimeUtc, started, DateTimeOffset.UtcNow);
            sessionState.SetAuthenticated(AuthSession.FromLoginResponse(response), true);
            return LoginResult.Succeeded(response.User.Name);
        }
        catch (AuthApiException ex)
        {
            sessionState.SetSignedOut();
            return LoginFailureMapper.FromApiException(ex);
        }
        catch (HttpRequestException)
        {
            sessionState.SetSignedOut();
            return LoginResult.Failed(LoginFailureKind.NetworkUnavailable,
                "Cannot reach Examifo. Check your internet connection and try again.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sessionState.SetSignedOut();
            return LoginResult.Failed(LoginFailureKind.Timeout, "The login request timed out. Please try again.");
        }
        catch (OperationCanceledException)
        {
            sessionState.SetSignedOut();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sessionState.SetSignedOut();
            System.Diagnostics.Debug.WriteLine($"Login failed: {ex}");
            return LoginResult.Failed(LoginFailureKind.LocalStorageFailure,
                "Unable to save your Examifo session on this device.");
        }
    }

    public async Task<string?> GetAccessTokenAsync() => (await sessionStore.LoadAsync())?.AccessToken;

    public async Task<Guid> GetDeviceIdAsync()
    {
        AuthSession session = await sessionStore.LoadAsync()
            ?? throw new AuthApiException(System.Net.HttpStatusCode.Unauthorized, "LOGIN_REQUIRED", "Sign in again.");
        return session.DeviceId;
    }

    public Task<AuthSession?> GetCurrentSessionAsync(CancellationToken cancellationToken = default) =>
        sessionStore.LoadAsync(cancellationToken);

    public async Task LogoutAsync(bool localOnly = false, CancellationToken cancellationToken = default)
        => await logoutService.LogoutAsync(localOnly, cancellationToken);

    public async Task<string> GetValidAccessTokenAsync(
        CancellationToken cancellationToken = default) =>
        await tokenRefreshCoordinator.GetValidAccessTokenAsync(cancellationToken);

    public Task<string> RefreshAfterUnauthorizedAsync(
        string rejectedAccessToken,
        CancellationToken cancellationToken = default) =>
        tokenRefreshCoordinator.RefreshAfterUnauthorizedAsync(rejectedAccessToken, cancellationToken);

}

public enum LoginFailureKind
{
    None, InvalidInput, InvalidCredentials, AccountNotConfirmed, AccountDisabled,
    NetworkUnavailable, Timeout, ServerRejected, InvalidServerResponse, LocalStorageFailure
}

public sealed record LoginResult(bool Success, string? UserName, LoginFailureKind FailureKind, string? ErrorMessage)
{
    public static LoginResult Succeeded(string userName) => new(true, userName, LoginFailureKind.None, null);
    public static LoginResult Failed(string message) => Failed(LoginFailureKind.InvalidInput, message);
    public static LoginResult Failed(LoginFailureKind kind, string message) => new(false, null, kind, message);
}

public static class LoginFailureMapper
{
    public static LoginResult FromApiException(AuthApiException exception) => exception.Code switch
    {
        "INVALID_LOGIN" => LoginResult.Failed(LoginFailureKind.InvalidInput,
            "Enter a valid email, password, and device information."),
        "INVALID_CREDENTIALS" => LoginResult.Failed(LoginFailureKind.InvalidCredentials, "Invalid email or password."),
        "ACCOUNT_NOT_CONFIRMED" => LoginResult.Failed(LoginFailureKind.AccountNotConfirmed,
            "Confirm your email address before signing in."),
        "ACCOUNT_DISABLED" => LoginResult.Failed(LoginFailureKind.AccountDisabled,
            "This account is disabled. Contact your examination administrator."),
        "INVALID_RESPONSE" => LoginResult.Failed(LoginFailureKind.InvalidServerResponse,
            "Examifo returned an invalid response. Please try again."),
        _ => LoginResult.Failed(LoginFailureKind.ServerRejected,
            string.IsNullOrWhiteSpace(exception.Code) ? "Unable to sign in to Examifo."
                : $"Unable to sign in to Examifo. Reference: {exception.Code}.")
    };
}
