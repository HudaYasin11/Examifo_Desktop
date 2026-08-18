namespace Examifo_Desktop.Infrastructure.Sync;

public interface INetworkAvailability
{
    bool IsInternetAvailable { get; }
    event EventHandler<bool>? AvailabilityChanged;
}
