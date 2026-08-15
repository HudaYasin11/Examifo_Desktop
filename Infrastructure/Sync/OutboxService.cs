using System;
namespace Examifo_Desktop.Infrastructure.Sync;

public sealed class OutboxService(Examifo_Desktop.Infrastructure.Persistence.DatabaseService databaseService)
{
    public Task<List<SyncOperation>> GetPendingAsync(int limit = 500, CancellationToken cancellationToken = default) =>
        databaseService.GetPendingOperationsAsync(limit, cancellationToken);

    public Task MarkResultAsync(Guid operationId, string state, string? errorCode, long? serverRevision,
        CancellationToken cancellationToken = default) =>
        databaseService.ApplySyncResultAsync(operationId, state, errorCode, serverRevision, cancellationToken);
}
