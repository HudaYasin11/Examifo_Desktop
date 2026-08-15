using System.Collections.Concurrent;
using System.Net;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Services;

DateTimeOffset now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

await Run("returns a sufficiently valid access token without refresh", async () =>
{
    var fixture = await Fixture.Create(now, accessExpiry: now.AddMinutes(2));
    Assert(await fixture.Coordinator.GetValidAccessTokenAsync() == "old-access", "Current token must be returned.");
    Assert(fixture.Client.CallCount == 0, "Refresh endpoint must not be called.");
});

await Run("rotates both tokens and saves the replacement session", async () =>
{
    var fixture = await Fixture.Create(now, accessExpiry: now.AddSeconds(10));
    Assert(await fixture.Coordinator.GetValidAccessTokenAsync() == "new-access", "New access token must be returned.");
    AuthSession? saved = await fixture.Store.LoadAsync();
    Assert(saved?.AccessToken == "new-access" && saved.RefreshToken == "new-refresh", "Both rotated tokens must be saved.");
});

await Run("concurrent expiry causes one refresh", async () =>
{
    var fixture = await Fixture.Create(now, accessExpiry: now.AddSeconds(10));
    string[] tokens = await Task.WhenAll(Enumerable.Range(0, 20)
        .Select(_ => fixture.Coordinator.GetValidAccessTokenAsync()));
    Assert(tokens.All(token => token == "new-access"), "All callers must receive the rotated token.");
    Assert(fixture.Client.CallCount == 1, "Only one refresh request may be sent.");
});

await Run("expired refresh token clears authentication", async () =>
{
    var fixture = await Fixture.Create(now, now.AddSeconds(10), now.AddSeconds(-1));
    await AssertAuthCode("INVALID_REFRESH_TOKEN", () => fixture.Coordinator.GetValidAccessTokenAsync());
    Assert(await fixture.Store.LoadAsync() is null, "Expired session must be cleared.");
});

await Run("revoked session clears authentication", async () =>
{
    var fixture = await Fixture.Create(now, accessExpiry: now.AddSeconds(10));
    fixture.Client.Exception = new AuthApiException(HttpStatusCode.Forbidden, "SESSION_REVOKED", "Revoked");
    await AssertAuthCode("SESSION_REVOKED", () => fixture.Coordinator.GetValidAccessTokenAsync());
    Assert(await fixture.Store.LoadAsync() is null, "Revoked session must be cleared.");
});

await Run("temporary failure preserves the existing session", async () =>
{
    var fixture = await Fixture.Create(now, accessExpiry: now.AddSeconds(10));
    fixture.Client.Exception = new AuthApiException(HttpStatusCode.ServiceUnavailable, "TEMPORARY", "Retry later");
    await AssertAuthCode("TEMPORARY", () => fixture.Coordinator.GetValidAccessTokenAsync());
    Assert((await fixture.Store.LoadAsync())?.RefreshToken == "old-refresh", "Temporary failure must preserve the session.");
});

await Run("invalid rotation preserves the last valid session", async () =>
{
    var fixture = await Fixture.Create(now, accessExpiry: now.AddSeconds(10));
    fixture.Client.Response = fixture.Client.Response with { RefreshToken = "old-refresh" };
    await AssertAuthCode("INVALID_RESPONSE", () => fixture.Coordinator.GetValidAccessTokenAsync());
    Assert((await fixture.Store.LoadAsync())?.RefreshToken == "old-refresh", "Invalid response must not overwrite the session.");
});

Console.WriteLine("All token refresh tests passed.");

static async Task AssertAuthCode(string code, Func<Task<string>> action)
{
    try { await action(); throw new InvalidOperationException("Expected AuthApiException."); }
    catch (AuthApiException ex) when (ex.Code == code) { }
}
static async Task Run(string name, Func<Task> test) { await test(); Console.WriteLine($"PASS: {name}"); }
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

file sealed class Fixture
{
    public required TokenRefreshCoordinator Coordinator { get; init; }
    public required AuthSessionStore Store { get; init; }
    public required FakeRefreshClient Client { get; init; }
    public static async Task<Fixture> Create(DateTimeOffset now, DateTimeOffset? accessExpiry = null, DateTimeOffset? refreshExpiry = null)
    {
        var store = new AuthSessionStore(new MemorySecureStore());
        Guid deviceId = Guid.NewGuid();
        await store.SaveAsync(new AuthSession(1, "old-access", accessExpiry ?? now.AddMinutes(2), "old-refresh",
            refreshExpiry ?? now.AddDays(1), deviceId, now, Guid.NewGuid(), "Student", null));
        var client = new FakeRefreshClient
        {
            Response = new LoginResponse("new-access", now.AddMinutes(10), "new-refresh", now.AddDays(2),
                deviceId, now, new AuthUserResponse(Guid.NewGuid(), "Student", null))
        };
        return new Fixture { Store = store, Client = client,
            Coordinator = new TokenRefreshCoordinator(client, store, new FixedTimeProvider(now)) };
    }
}
file sealed class FakeRefreshClient : ITokenRefreshClient
{
    public required LoginResponse Response { get; set; }
    public AuthApiException? Exception { get; set; }
    public int CallCount;
    public async Task<LoginResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref CallCount); await Task.Delay(10, cancellationToken);
        if (Exception is not null) throw Exception; return Response;
    }
}
file sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
file sealed class MemorySecureStore : ISecureValueStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();
    public Task<string?> GetAsync(string key) => Task.FromResult(_values.TryGetValue(key, out string? value) ? value : null);
    public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
    public void Remove(string key) => _values.TryRemove(key, out _);
}
