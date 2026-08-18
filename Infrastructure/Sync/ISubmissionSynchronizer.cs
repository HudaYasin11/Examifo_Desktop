namespace Examifo_Desktop.Infrastructure.Sync;

public interface ISubmissionSynchronizer
{
    Task SyncPendingAsync(CancellationToken cancellationToken = default);
}
