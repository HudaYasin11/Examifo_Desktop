using System;
namespace Examifo_Desktop.Infrastructure.Sync;

public sealed class OutboxService(Examifo_Desktop.Infrastructure.Persistence.DatabaseService databaseService)
{
    public Task<List<SyncOperation>> ClaimPendingAsync(int limit = 500, CancellationToken cancellationToken = default) =>
        databaseService.ClaimPendingOperationsAsync(limit, cancellationToken);

    public Task ReturnForRetryAsync(IEnumerable<Guid> operationIds, DateTime nextAttemptAtUtc,
        CancellationToken cancellationToken = default) =>
        databaseService.ReturnOperationsForRetryAsync(operationIds, nextAttemptAtUtc, cancellationToken);

    public Task RecoverStaleInFlightAsync(DateTime staleBeforeUtc,
        CancellationToken cancellationToken = default) =>
        databaseService.RecoverStaleInFlightAsync(staleBeforeUtc, cancellationToken);

    public Task MarkResultAsync(Guid operationId, string state, string? errorCode, long? serverRevision,
        CancellationToken cancellationToken = default) =>
        databaseService.ApplySyncResultAsync(operationId, state, errorCode, serverRevision, cancellationToken);

    public Task AdvanceCheckpointAsync(Guid clientId, long serverRevision, DateTime successfulSyncUtc,
        string? pullCursor = null, CancellationToken cancellationToken = default) =>
        databaseService.AdvanceSyncCheckpointAsync(
            clientId, serverRevision, successfulSyncUtc, pullCursor, cancellationToken);

    public Task<Examifo_Desktop.Infrastructure.Persistence.SyncCheckpointRecord?> GetCheckpointAsync(
        Guid clientId, CancellationToken cancellationToken = default) =>
        databaseService.GetSyncCheckpointAsync(clientId, cancellationToken);
}
