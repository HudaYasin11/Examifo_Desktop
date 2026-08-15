using System.Collections.Concurrent;
using Examifo_Desktop.Services;

Run("creates and persists a non-empty ID", () =>
{
    var store = new MemoryStore();
    Guid id = new InstallationIdentityService(store).GetOrCreateInstallationId();

    Assert(id != Guid.Empty, "Generated ID must not be empty.");
    Assert(store.Values[InstallationIdentityService.InstallationIdKey] == id.ToString("D"),
        "Generated ID must be persisted in canonical format.");
});

Run("reuses a valid persisted ID after restart", () =>
{
    Guid expected = Guid.NewGuid();
    var store = new MemoryStore(expected.ToString("D"));

    Guid actual = new InstallationIdentityService(store).GetOrCreateInstallationId();

    Assert(actual == expected, "A valid persisted ID must be reused.");
    Assert(store.WriteCount == 0, "A valid persisted ID must not be rewritten.");
});

Run("replaces corrupt and empty IDs", () =>
{
    foreach (string invalidValue in new[] { "not-a-guid", Guid.Empty.ToString("D"), "" })
    {
        var store = new MemoryStore(invalidValue);
        Guid actual = new InstallationIdentityService(store).GetOrCreateInstallationId();

        Assert(actual != Guid.Empty, "Invalid storage must be replaced with a non-empty ID.");
        Assert(store.WriteCount == 1, "Invalid storage must be replaced exactly once.");
    }
});

Run("returns one ID to concurrent callers", () =>
{
    var store = new MemoryStore();
    var service = new InstallationIdentityService(store);

    Guid[] ids = Enumerable.Range(0, 100)
        .AsParallel()
        .Select(_ => service.GetOrCreateInstallationId())
        .ToArray();

    Assert(ids.Distinct().Count() == 1, "Concurrent callers must receive one ID.");
    Assert(store.WriteCount == 1, "Concurrent initialization must write only once.");
});

Console.WriteLine("All installation identity tests passed.");

static void Run(string name, Action test)
{
    test();
    Console.WriteLine($"PASS: {name}");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

file sealed class MemoryStore : IInstallationIdentityStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new();
    public int WriteCount;

    public MemoryStore(string? initialValue = null)
    {
        if (initialValue is not null)
            Values[InstallationIdentityService.InstallationIdKey] = initialValue;
    }

    public string? Get(string key) => Values.TryGetValue(key, out string? value) ? value : null;

    public void Set(string key, string value)
    {
        Interlocked.Increment(ref WriteCount);
        Values[key] = value;
    }
}
