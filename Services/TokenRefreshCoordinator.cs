using System.Net;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Services;

public interface ITokenRefreshClient
{
    Task<LoginResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default);
}

public sealed class TokenRefreshCoordinator(
    ITokenRefreshClient refreshClient,
    AuthSessionStore sessionStore,
    TimeProvider timeProvider,
    TrustedServerTimeService? trustedTime = null,
    SessionStateService? sessionState = null) : IAuthenticatedTokenProvider
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public async Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        AuthSession? session = await sessionStore.LoadAsync(cancellationToken);
        if (session is not null && session.AccessTokenExpiresAtUtc > UtcNow.Add(RefreshSkew))
            return session.AccessToken;

        return await RefreshAsync(null, cancellationToken);
    }

    public Task<string> RefreshAfterUnauthorizedAsync(string rejectedAccessToken,
        CancellationToken cancellationToken = default) => RefreshAsync(rejectedAccessToken, cancellationToken);

    private async Task<string> RefreshAsync(string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            AuthSession? session = await sessionStore.LoadAsync(cancellationToken);
            if (rejectedAccessToken is not null && session is not null
                && !string.Equals(session.AccessToken, rejectedAccessToken, StringComparison.Ordinal))
                return session.AccessToken;

            if (rejectedAccessToken is null && session is not null
                && session.AccessTokenExpiresAtUtc > UtcNow.Add(RefreshSkew))
                return session.AccessToken;

            if (session is null)
                throw LoginRequired("LOGIN_REQUIRED", "Sign in again.");

            if (session.RefreshTokenExpiresAtUtc <= UtcNow)
            {
                await sessionStore.ClearAsync(cancellationToken);
                sessionState?.RequireReauthentication("INVALID_REFRESH_TOKEN");
                throw LoginRequired("INVALID_REFRESH_TOKEN", "Your session has expired. Sign in again.");
            }

            try
            {
                DateTimeOffset started = timeProvider.GetUtcNow();
                LoginResponse response = await refreshClient.RefreshAsync(
                    new RefreshRequest(session.RefreshToken, session.DeviceId), cancellationToken);
                ValidateRotation(session, response);
                await sessionStore.SaveAsync(AuthSession.FromLoginResponse(response), cancellationToken);
                trustedTime?.RecordSample(response.ServerTimeUtc, started, timeProvider.GetUtcNow());
                return response.AccessToken;
            }
            catch (AuthApiException ex) when (ex.Code is "INVALID_REFRESH_TOKEN" or "SESSION_REVOKED")
            {
                await sessionStore.ClearAsync(cancellationToken);
                sessionState?.RequireReauthentication(ex.Code ?? "SESSION_REVOKED");
                throw;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static void ValidateRotation(AuthSession previous, LoginResponse response)
    {
        if (response.DeviceId != previous.DeviceId
            || string.Equals(response.AccessToken, previous.AccessToken, StringComparison.Ordinal)
            || string.Equals(response.RefreshToken, previous.RefreshToken, StringComparison.Ordinal))
            throw new AuthApiException(HttpStatusCode.BadGateway, "INVALID_RESPONSE",
                "Examifo returned an invalid rotated session.");
    }

    private DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    private static AuthApiException LoginRequired(string code, string message) =>
        new(HttpStatusCode.Unauthorized, code, message);
}
