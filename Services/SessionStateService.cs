namespace Examifo_Desktop.Services;

public enum SessionState
{
    SignedOut,
    Restoring,
    SigningIn,
    AuthenticatedOnline,
    AuthenticatedOffline,
    SigningOut,
    ReauthenticationRequired
}

public sealed record SessionSnapshot(SessionState State, Guid? UserId = null, Guid? DeviceId = null,
    string? UserName = null, string? Reason = null);

public sealed class SessionStateService
{
    private readonly object _gate = new();
    private SessionSnapshot _current = new(SessionState.SignedOut);
    public SessionSnapshot Current { get { lock (_gate) return _current; } }
    public event EventHandler<SessionSnapshot>? Changed;

    public void BeginRestore() => Transition(new(SessionState.Restoring), SessionState.SignedOut);
    public void BeginSignIn() => Transition(new(SessionState.SigningIn), SessionState.SignedOut,
        SessionState.ReauthenticationRequired);
    public void SetAuthenticated(AuthSession session, bool online) => Transition(new(
        online ? SessionState.AuthenticatedOnline : SessionState.AuthenticatedOffline,
        session.UserId, session.DeviceId, session.UserName), SessionState.Restoring, SessionState.SigningIn,
        SessionState.AuthenticatedOnline, SessionState.AuthenticatedOffline, SessionState.SigningOut);
    public void BeginSignOut() => Transition(new(SessionState.SigningOut),
        SessionState.AuthenticatedOnline, SessionState.AuthenticatedOffline, SessionState.ReauthenticationRequired,
        SessionState.SigningOut);
    public void SetSignedOut() => Transition(new(SessionState.SignedOut), Enum.GetValues<SessionState>());
    public void RequireReauthentication(string reason) => Transition(
        new(SessionState.ReauthenticationRequired, Reason: reason), Enum.GetValues<SessionState>());

    private void Transition(SessionSnapshot next, params SessionState[] allowed)
    {
        EventHandler<SessionSnapshot>? changed;
        lock (_gate)
        {
            if (!allowed.Contains(_current.State))
                throw new InvalidOperationException($"Invalid session transition: {_current.State} -> {next.State}.");
            _current = next;
            changed = Changed;
        }
        changed?.Invoke(this, next);
    }
}
