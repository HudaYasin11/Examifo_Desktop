using System.Collections.Concurrent;
using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Services;

await TestAuthorizationSurvivesRestartAsync();
await TestAuthorizationReplacementAndRemovalAsync();
await TestCorruptAuthorizationIsRemovedAsync();
await TestNoSessionAsync();
await TestVerifiedIdentityAsync();
await TestIdentityMismatchClearsSessionAsync();
await TestOfflineRestorePreservesSessionAsync();
await TestRejectedSessionIsClearedAsync();
await PortionCompletionTests.TestLogoutLifecycleAsync();
PortionCompletionTests.TestTrustedServerTime();
PortionCompletionTests.TestSessionTransitions();
Console.WriteLine("All offline authorization and session restoration tests passed.");

static async Task TestAuthorizationSurvivesRestartAsync()
{
    var secure = new MemoryStore();
    StoredOfflineAuthorization expected = Authorization();
    await new OfflineAuthorizationStore(secure).SaveAsync(expected);
    StoredOfflineAuthorization? restored = await new OfflineAuthorizationStore(secure).FindForExamAsync(expected.ExamId);
    Assert(restored == expected && secure.Values[OfflineAuthorizationStore.StorageKey].Contains(expected.AuthorizationToken),
        "one-time authorization survives restart in secure storage");
}

static async Task TestAuthorizationReplacementAndRemovalAsync()
{
    var secure = new MemoryStore();
    var store = new OfflineAuthorizationStore(secure);
    StoredOfflineAuthorization first = Authorization();
    StoredOfflineAuthorization replacement = Authorization() with { ExamId = first.ExamId };
    await store.SaveAsync(first);
    await store.SaveAsync(replacement);
    Assert((await store.FindForExamAsync(first.ExamId))?.AuthorizationId == replacement.AuthorizationId,
        "new authorization replaces stale authorization for the exam");
    await store.RemoveAsync(replacement.AuthorizationId);
    Assert(await store.FindForExamAsync(first.ExamId) is null && !secure.Values.ContainsKey(OfflineAuthorizationStore.StorageKey),
        "consumed or cancelled authorization is removed");
}

static async Task TestCorruptAuthorizationIsRemovedAsync()
{
    var secure = new MemoryStore();
    secure.Values[OfflineAuthorizationStore.StorageKey] = "{broken";
    Assert(await new OfflineAuthorizationStore(secure).FindForExamAsync(Guid.NewGuid()) is null
        && !secure.Values.ContainsKey(OfflineAuthorizationStore.StorageKey),
        "corrupt authorization state is rejected and removed");
}

static async Task TestNoSessionAsync()
{
    var fixture = new RestoreFixture();
    SessionRestoreResult result = await fixture.Restorer.RestoreAsync();
    Assert(result.Status == SessionRestoreStatus.NoSession && fixture.Identity.Calls == 0,
        "startup without stored session opens unauthenticated flow");
}

static async Task TestVerifiedIdentityAsync()
{
    var fixture = new RestoreFixture();
    AuthSession session = await fixture.SaveSessionAsync();
    fixture.Identity.Response = new(new AuthUserResponse(session.UserId!.Value, "Verified Student", "v@example.com"),
        session.DeviceId, DateTimeOffset.UtcNow);
    SessionRestoreResult result = await fixture.Restorer.RestoreAsync();
    AuthSession? saved = await fixture.SessionStore.LoadAsync();
    Assert(result.Status == SessionRestoreStatus.VerifiedOnline && saved?.UserName == "Verified Student",
        "/auth/me verifies and refreshes saved identity");
}

static async Task TestIdentityMismatchClearsSessionAsync()
{
    var fixture = new RestoreFixture();
    AuthSession session = await fixture.SaveSessionAsync();
    fixture.Identity.Response = new(new AuthUserResponse(Guid.NewGuid(), "Other", null), session.DeviceId,
        DateTimeOffset.UtcNow);
    Assert((await fixture.Restorer.RestoreAsync()).Status == SessionRestoreStatus.Rejected
        && await fixture.SessionStore.LoadAsync() is null, "user mismatch rejects and clears session");
}

static async Task TestOfflineRestorePreservesSessionAsync()
{
    var fixture = new RestoreFixture();
    await fixture.SaveSessionAsync();
    fixture.Identity.Exception = new HttpRequestException("offline");
    Assert((await fixture.Restorer.RestoreAsync()).Status == SessionRestoreStatus.AvailableOffline
        && await fixture.SessionStore.LoadAsync() is not null, "network failure preserves offline session");
}

static async Task TestRejectedSessionIsClearedAsync()
{
    var fixture = new RestoreFixture();
    await fixture.SaveSessionAsync();
    fixture.Identity.Exception = new AuthApiException(System.Net.HttpStatusCode.Unauthorized,
        "SESSION_REJECTED", "rejected");
    Assert((await fixture.Restorer.RestoreAsync()).Status == SessionRestoreStatus.Rejected
        && await fixture.SessionStore.LoadAsync() is null, "server rejection clears stored session");
}

static StoredOfflineAuthorization Authorization()
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
        now.AddMinutes(-1), now.AddHours(1), now.AddHours(2), 3600, 1, "shuffle-seed", now, "one-time-token");
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}

sealed class RestoreFixture
{
    private readonly MemoryStore _secure = new();
    public AuthSessionStore SessionStore { get; }
    public FakeIdentity Identity { get; } = new();
    public SessionRestorationService Restorer { get; }
    public RestoreFixture()
    {
        SessionStore = new AuthSessionStore(_secure);
        Restorer = new SessionRestorationService(SessionStore, new FakeTokens(), Identity,
            new TrustedServerTimeService(new MemoryTimeStore(), TimeProvider.System), new SessionStateService());
    }
    public async Task<AuthSession> SaveSessionAsync()
    {
        var session = new AuthSession(1, "access", DateTimeOffset.UtcNow.AddMinutes(10), "refresh",
            DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid(), DateTimeOffset.UtcNow,
            Guid.NewGuid(), "Student", null);
        await SessionStore.SaveAsync(session);
        Identity.Response = new(new AuthUserResponse(session.UserId!.Value, "Student", null),
            session.DeviceId, DateTimeOffset.UtcNow);
        return session;
    }
}

sealed class FakeTokens : IAuthenticatedTokenProvider
{
    public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("access");
    public Task<string> RefreshAfterUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default) =>
        Task.FromResult("rotated");
}

sealed class FakeIdentity : ICurrentIdentityClient
{
    public int Calls;
    public CurrentIdentityResponse Response { get; set; } = new(new AuthUserResponse(Guid.NewGuid(), "Student", null),
        Guid.NewGuid(), DateTimeOffset.UtcNow);
    public Exception? Exception { get; set; }
    public Task<CurrentIdentityResponse> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Exception is null ? Task.FromResult(Response) : Task.FromException<CurrentIdentityResponse>(Exception);
    }
    public int LogoutCalls;
    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        LogoutCalls++;
        return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
    }
}

static class PortionCompletionTests
{
public static async Task TestLogoutLifecycleAsync()
{
    var secure = new MemoryStore();
    var store = new AuthSessionStore(secure);
    AuthSession session = new(1, "access", DateTimeOffset.UtcNow.AddMinutes(5), "refresh",
        DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid(), null, Guid.NewGuid(), "Student", null);
    await store.SaveAsync(session);
    var identity = new FakeIdentity();
    var state = new SessionStateService();
    state.BeginRestore();
    state.SetAuthenticated(session, true);
    await new SessionLogoutService(identity, store, state).LogoutAsync();
    Assert(identity.LogoutCalls == 1 && await store.LoadAsync() is null
        && state.Current.State == SessionState.SignedOut, "server logout clears secrets and central state");

    await store.SaveAsync(session);
    state.BeginSignIn();
    state.SetAuthenticated(session, true);
    identity.Exception = new HttpRequestException("offline");
    try { await new SessionLogoutService(identity, store, state).LogoutAsync(); } catch (HttpRequestException) { }
    Assert(await store.LoadAsync() is not null && state.Current.State == SessionState.AuthenticatedOffline,
        "failed server logout preserves session until local-only consent");
    identity.Exception = null;
    await new SessionLogoutService(identity, store, state).LogoutAsync(localOnly: true);
    Assert(await store.LoadAsync() is null, "explicit local-only logout clears local secrets without server call");
}

public static void TestTrustedServerTime()
{
    DateTimeOffset local = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    var timeStore = new MemoryTimeStore();
    var time = new TrustedServerTimeService(timeStore, new FixedTimeProvider(local));
    time.RecordSample(local.AddMinutes(5).AddMilliseconds(100), local, local.AddMilliseconds(200));
    Assert(time.HasTrustedOffset && time.UtcNow == local.AddMinutes(5),
        "trusted time uses midpoint-adjusted server offset");
    var restarted = new TrustedServerTimeService(timeStore, new FixedTimeProvider(local));
    Assert(restarted.UtcNow == local.AddMinutes(5), "trusted time offset survives restart");
    Assert(restarted.CalculateDeadline(local, 3600, local.AddMinutes(30)) == local.AddMinutes(30),
        "deadline uses the earlier duration or server submission limit");
}

public static void TestSessionTransitions()
{
    var state = new SessionStateService();
    state.BeginRestore();
    AuthSession session = new(1, "a", DateTimeOffset.UtcNow.AddMinutes(1), "r",
        DateTimeOffset.UtcNow.AddDays(1), Guid.NewGuid(), null, Guid.NewGuid(), "Student", null);
    state.SetAuthenticated(session, false);
    state.BeginSignOut();
    state.BeginSignOut();
    state.SetSignedOut();
    Assert(state.Current.State == SessionState.SignedOut, "central state follows guarded lifecycle transitions");
    try { state.BeginSignOut(); throw new InvalidOperationException("FAIL: invalid transition rejected"); }
    catch (InvalidOperationException) { Console.WriteLine("PASS: invalid session transition is rejected"); }
}

private static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}
}

sealed class MemoryTimeStore : ITrustedTimeStore
{
    private long? _ticks;
    public long? GetOffsetTicks() => _ticks;
    public void SetOffsetTicks(long offsetTicks) => _ticks = offsetTicks;
}

sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class MemoryStore : ISecureValueStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new();
    public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out string? value) ? value : null);
    public Task SetAsync(string key, string value) { Values[key] = value; return Task.CompletedTask; }
    public void Remove(string key) => Values.TryRemove(key, out _);
}
