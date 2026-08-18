namespace Examifo_Desktop.Infrastructure.Sync;

public sealed class MauiNetworkAvailability : INetworkAvailability, IDisposable
{
    private readonly Microsoft.Maui.Networking.IConnectivity _connectivity;

    public MauiNetworkAvailability(Microsoft.Maui.Networking.IConnectivity connectivity)
    {
        _connectivity = connectivity;
        _connectivity.ConnectivityChanged += ConnectivityChanged;
    }

    public bool IsInternetAvailable =>
        _connectivity.NetworkAccess == Microsoft.Maui.Networking.NetworkAccess.Internet;

    public event EventHandler<bool>? AvailabilityChanged;

    private void ConnectivityChanged(object? sender, Microsoft.Maui.Networking.ConnectivityChangedEventArgs e) =>
        AvailabilityChanged?.Invoke(this,
            e.NetworkAccess == Microsoft.Maui.Networking.NetworkAccess.Internet);

    public void Dispose() => _connectivity.ConnectivityChanged -= ConnectivityChanged;
}
