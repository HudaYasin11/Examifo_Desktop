using System.Collections.Concurrent;
using Examifo_Desktop.Services;

await Run("saves and loads one complete envelope", async () =>
{
    var secure = new MemorySecureStore();
    var store = new AuthSessionStore(secure);
    AuthSession expected = ValidSession();
    await store.SaveAsync(expected);
    AuthSession? actual = await store.LoadAsync();
    Assert(actual == expected, "Saved session must round-trip as one envelope.");
    Assert(secure.Values.Keys.SequenceEqual([AuthSessionStore.SessionKey]), "Only the envelope key may remain.");
});

await Run("rejects corrupt envelopes", async () =>
{
    var secure = new MemorySecureStore();
    secure.Values[AuthSessionStore.SessionKey] = "{broken";
    Assert(await new AuthSessionStore(secure).LoadAsync() is null, "Corrupt session must not be returned.");
    Assert(!secure.Values.ContainsKey(AuthSessionStore.SessionKey), "Corrupt session must be removed.");
});

await Run("migrates a complete legacy session", async () =>
{
    Guid deviceId = Guid.NewGuid();
    var secure = new MemorySecureStore(new Dictionary<string, string>
    {
        ["examifo.access_token"] = "access",
        ["examifo.access_token_expiry"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
        ["examifo.refresh_token"] = "refresh",
        ["examifo.refresh_token_expiry"] = DateTimeOffset.UtcNow.AddDays(10).ToString("O"),
        ["examifo.device_id"] = deviceId.ToString("D")
    });
    AuthSession? session = await new AuthSessionStore(secure).LoadAsync();
    Assert(session?.DeviceId == deviceId, "Legacy device ID must migrate.");
    Assert(secure.Values.ContainsKey(AuthSessionStore.SessionKey), "Migrated envelope must be stored.");
    Assert(secure.Values.Count == 1, "Legacy keys must be removed after migration.");
});

await Run("clears partial legacy state", async () =>
{
    var secure = new MemorySecureStore(new Dictionary<string, string>
    {
        ["examifo.access_token"] = "access",
        ["examifo.device_id"] = Guid.NewGuid().ToString("D")
    });
    Assert(await new AuthSessionStore(secure).LoadAsync() is null, "Partial legacy state must not authenticate.");
    Assert(secure.Values.IsEmpty, "Partial legacy secrets must be removed.");
});

await Run("clear removes envelope and all legacy keys", async () =>
{
    var secure = new MemorySecureStore();
    await new AuthSessionStore(secure).SaveAsync(ValidSession());
    secure.Values["examifo.access_token"] = "stale";
    await new AuthSessionStore(secure).ClearAsync();
    Assert(secure.Values.IsEmpty, "Clear must remove every session representation.");
});

Console.WriteLine("All authentication session-store tests passed.");

static AuthSession ValidSession() => new(1, "access", DateTimeOffset.UtcNow.AddMinutes(10), "refresh",
    DateTimeOffset.UtcNow.AddDays(10), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "Student", "s@example.com");
static async Task Run(string name, Func<Task> test) { await test(); Console.WriteLine($"PASS: {name}"); }
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

file sealed class MemorySecureStore : ISecureValueStore
{
    public ConcurrentDictionary<string, string> Values { get; }
    public MemorySecureStore(Dictionary<string, string>? values = null) => Values = new(values ?? []);
    public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out string? value) ? value : null);
    public Task SetAsync(string key, string value) { Values[key] = value; return Task.CompletedTask; }
    public void Remove(string key) => Values.TryRemove(key, out _);
}
