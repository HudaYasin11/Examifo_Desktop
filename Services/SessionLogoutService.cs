using Examifo_Desktop.Infrastructure.Api.Clients;

namespace Examifo_Desktop.Services;

public sealed class SessionLogoutService(
    ICurrentIdentityClient identityClient,
    AuthSessionStore sessionStore,
    SessionStateService sessionState)
{
    public async Task LogoutAsync(bool localOnly = false, CancellationToken cancellationToken = default)
    {
        if (sessionState.Current.State == SessionState.SignedOut)
        {
            await sessionStore.ClearAsync(cancellationToken);
            return;
        }

        sessionState.BeginSignOut();
        try
        {
            if (!localOnly) await identityClient.LogoutAsync(cancellationToken);
            await sessionStore.ClearAsync(cancellationToken);
            sessionState.SetSignedOut();
        }
        catch
        {
            if (localOnly)
            {
                await sessionStore.ClearAsync(cancellationToken);
                sessionState.SetSignedOut();
            }
            else
            {
                AuthSession? session = await sessionStore.LoadAsync(cancellationToken);
                if (session is not null)
                    sessionState.SetAuthenticated(session, false);
                else
                    sessionState.SetSignedOut();
            }
            throw;
        }
    }
}
