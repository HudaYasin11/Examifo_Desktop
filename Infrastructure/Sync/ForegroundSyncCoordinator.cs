using Examifo_Desktop.Services;

namespace Examifo_Desktop.Infrastructure.Sync;

public sealed class ForegroundSyncCoordinator : IDisposable
{
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromSeconds(30);
    private readonly ISubmissionSynchronizer _synchronizer;
    private readonly INetworkAvailability _network;
    private readonly SessionStateService _sessionState;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private Task? _periodicTask;
    private bool _started;
    private volatile bool _foreground;
    private int _rerunRequested;

    public ForegroundSyncCoordinator(ISubmissionSynchronizer synchronizer,
        INetworkAvailability network, SessionStateService sessionState)
    {
        _synchronizer = synchronizer;
        _network = network;
        _sessionState = sessionState;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _foreground = true;
        _network.AvailabilityChanged += NetworkAvailabilityChanged;
        _sessionState.Changed += SessionStateChanged;
        _periodicTask = RunPeriodicAsync(_lifetime.Token);
        RequestSync();
    }

    public void SetForeground(bool foreground)
    {
        _foreground = foreground;
        if (foreground) RequestSync();
    }

    public async Task RunNowAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSynchronize()) return;
        await _runGate.WaitAsync(cancellationToken);
        try
        {
            do
            {
                Interlocked.Exchange(ref _rerunRequested, 0);
                if (!CanSynchronize()) return;
                await _synchronizer.SyncPendingAsync(cancellationToken);
            }
            while (Interlocked.Exchange(ref _rerunRequested, 0) == 1);
        }
        finally { _runGate.Release(); }
    }

    private bool CanSynchronize()
    {
        SessionState state = _sessionState.Current.State;
        return _foreground && _network.IsInternetAvailable
            && state is SessionState.AuthenticatedOnline or SessionState.AuthenticatedOffline;
    }

    private void RequestSync()
    {
        if (!CanSynchronize()) return;
        if (_runGate.CurrentCount == 0)
        {
            Interlocked.Exchange(ref _rerunRequested, 1);
            return;
        }
        _ = RunSafelyAsync();
    }

    private async Task RunSafelyAsync()
    {
        try { await RunNowAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Background synchronization will retry later: {ex}");
        }
    }

    private async Task RunPeriodicAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PeriodicInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)) RequestSync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void NetworkAvailabilityChanged(object? sender, bool online)
    {
        if (online) RequestSync();
    }

    private void SessionStateChanged(object? sender, SessionSnapshot snapshot)
    {
        if (snapshot.State is SessionState.AuthenticatedOnline or SessionState.AuthenticatedOffline)
            RequestSync();
    }

    public void Dispose()
    {
        if (!_started) return;
        _network.AvailabilityChanged -= NetworkAvailabilityChanged;
        _sessionState.Changed -= SessionStateChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _runGate.Dispose();
        _started = false;
    }
}
