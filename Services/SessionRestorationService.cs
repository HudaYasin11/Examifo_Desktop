using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Services;

public enum SessionRestoreStatus { NoSession, VerifiedOnline, AvailableOffline, Rejected }
public sealed record SessionRestoreResult(SessionRestoreStatus Status, string? UserName = null);

public sealed class SessionRestorationService(
    AuthSessionStore sessionStore,
    IAuthenticatedTokenProvider tokenProvider,
    ICurrentIdentityClient identityClient,
    TrustedServerTimeService trustedTime,
    SessionStateService sessionState)
{
    public async Task<SessionRestoreResult> RestoreAsync(CancellationToken cancellationToken = default)
    {
        sessionState.BeginRestore();
        AuthSession? session = await sessionStore.LoadAsync(cancellationToken);
        if (session is null)
        {
            sessionState.SetSignedOut();
            return new(SessionRestoreStatus.NoSession);
        }
        try
        {
            await tokenProvider.GetValidAccessTokenAsync(cancellationToken);
            DateTimeOffset started = DateTimeOffset.UtcNow;
            CurrentIdentityResponse identity = await identityClient.GetCurrentAsync(cancellationToken);
            trustedTime.RecordSample(identity.ServerTimeUtc, started, DateTimeOffset.UtcNow);
            if (identity.DeviceId != session.DeviceId
                || session.UserId is { } savedUserId && savedUserId != identity.User.Id)
            {
                await sessionStore.ClearAsync(cancellationToken);
                sessionState.RequireReauthentication("IDENTITY_MISMATCH");
                return new(SessionRestoreStatus.Rejected);
            }
            await sessionStore.SaveAsync(session with
            {
                ServerTimeUtc = identity.ServerTimeUtc,
                UserId = identity.User.Id,
                UserName = identity.User.Name,
                UserEmail = identity.User.Email
            }, cancellationToken);
            AuthSession verified = (await sessionStore.LoadAsync(cancellationToken))!;
            sessionState.SetAuthenticated(verified, true);
            return new(SessionRestoreStatus.VerifiedOnline, identity.User.Name);
        }
        catch (HttpRequestException)
        {
            sessionState.SetAuthenticated(session, false);
            return new(SessionRestoreStatus.AvailableOffline, session.UserName);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sessionState.SetAuthenticated(session, false);
            return new(SessionRestoreStatus.AvailableOffline, session.UserName);
        }
        catch (OperationCanceledException)
        {
            sessionState.SetSignedOut();
            throw;
        }
        catch (AuthApiException)
        {
            await sessionStore.ClearAsync(cancellationToken);
            sessionState.RequireReauthentication("SESSION_REJECTED");
            return new(SessionRestoreStatus.Rejected);
        }
        catch (InvalidDataException)
        {
            await sessionStore.ClearAsync(cancellationToken);
            sessionState.RequireReauthentication("INVALID_IDENTITY_RESPONSE");
            return new(SessionRestoreStatus.Rejected);
        }
    }
}
