using Examifo_Desktop.Infrastructure.Sync;
using Examifo_Desktop.Services;

await TestStateAndNetworkGuardsAsync();
await TestReconnectAndResumeAsync();
await TestOverlappingTriggersAreSerializedAsync();
Console.WriteLine("All lifecycle synchronization tests passed.");

static async Task TestStateAndNetworkGuardsAsync()
{
    var runner = new FakeSynchronizer();
    var network = new FakeNetwork(false);
    var session = new SessionStateService();
    using var coordinator = new ForegroundSyncCoordinator(runner, network, session);
    coordinator.Start();
    await coordinator.RunNowAsync();
    Assert(runner.Calls == 0, "signed-out startup does not synchronize");

    session.BeginRestore();
    session.SetAuthenticated(Session(), online: false);
    await coordinator.RunNowAsync();
    Assert(runner.Calls == 0, "offline authenticated state does not transmit");

    network.SetOnline(true);
    await WaitUntilAsync(() => runner.Calls == 1);
    coordinator.SetForeground(false);
    await coordinator.RunNowAsync();
    Assert(runner.Calls == 1, "stopped application does not synchronize");
}

static async Task TestReconnectAndResumeAsync()
{
    var runner = new FakeSynchronizer();
    var network = new FakeNetwork(true);
    var session = AuthenticatedSessionState();
    using var coordinator = new ForegroundSyncCoordinator(runner, network, session);
    coordinator.Start();
    await WaitUntilAsync(() => runner.Calls == 1);
    Assert(runner.Calls == 1, "authenticated startup triggers synchronization");

    coordinator.SetForeground(false);
    network.SetOnline(false);
    network.SetOnline(true);
    await Task.Delay(50);
    Assert(runner.Calls == 1, "reconnect while stopped waits for resume");

    coordinator.SetForeground(true);
    await WaitUntilAsync(() => runner.Calls == 2);
    Assert(runner.Calls == 2, "resume triggers synchronization after reconnect");
}

static async Task TestOverlappingTriggersAreSerializedAsync()
{
    var runner = new FakeSynchronizer(delayMilliseconds: 80);
    var network = new FakeNetwork(true);
    var session = AuthenticatedSessionState();
    using var coordinator = new ForegroundSyncCoordinator(runner, network, session);
    coordinator.Start();
    coordinator.SetForeground(true);
    network.SetOnline(true);
    await WaitUntilAsync(() => runner.Calls >= 2);
    Assert(runner.MaximumConcurrency == 1, "overlapping lifecycle triggers are serialized");
}

static SessionStateService AuthenticatedSessionState()
{
    var state = new SessionStateService();
    state.BeginRestore();
    state.SetAuthenticated(Session(), online: true);
    return state;
}

static AuthSession Session() => new(AuthSession.CurrentSchemaVersion, "access",
    DateTimeOffset.UtcNow.AddMinutes(5), "refresh", DateTimeOffset.UtcNow.AddDays(1),
    Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "Candidate", "candidate@example.com");

static async Task WaitUntilAsync(Func<bool> condition)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(2);
    while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    if (!condition()) throw new Exception("Timed out waiting for lifecycle synchronization.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception($"FAIL: {message}");
    Console.WriteLine($"PASS: {message}");
}

sealed class FakeNetwork(bool online) : INetworkAvailability
{
    public bool IsInternetAvailable { get; private set; } = online;
    public event EventHandler<bool>? AvailabilityChanged;
    public void SetOnline(bool online)
    {
        IsInternetAvailable = online;
        AvailabilityChanged?.Invoke(this, online);
    }
}

sealed class FakeSynchronizer(int delayMilliseconds = 0) : ISubmissionSynchronizer
{
    private int _calls;
    private int _concurrency;
    private int _maximumConcurrency;
    public int Calls => Volatile.Read(ref _calls);
    public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

    public async Task SyncPendingAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        int concurrency = Interlocked.Increment(ref _concurrency);
        int observed;
        while (concurrency > (observed = _maximumConcurrency))
            Interlocked.CompareExchange(ref _maximumConcurrency, concurrency, observed);
        try
        {
            if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
        }
        finally { Interlocked.Decrement(ref _concurrency); }
    }
}
