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

    public Task ApplyPulledChangesAsync(Guid clientId, long nextRevision,
        IReadOnlyList<PulledSyncChange> changes, DateTime successfulSyncUtc,
        CancellationToken cancellationToken = default) =>
        databaseService.ApplyPulledChangesAsync(
            clientId, nextRevision, changes, successfulSyncUtc, cancellationToken);

    public Task<List<SyncOperation>> GetStaleInFlightAsync(DateTime staleBeforeUtc,
        CancellationToken cancellationToken = default) =>
        databaseService.GetStaleInFlightOperationsAsync(staleBeforeUtc, cancellationToken);

    public Task<List<Examifo_Desktop.Domain.Models.Attempt>> GetAttemptsRequiringRecoveryAsync(
        CancellationToken cancellationToken = default) =>
        databaseService.GetAttemptsRequiringAuthoritativeRecoveryAsync(cancellationToken);

    public Task ApplyAttemptSummaryAsync(Guid attemptId, string status, long lastAcceptedSequence,
        int answerCount, bool submitted, long serverRevision,
        CancellationToken cancellationToken = default) =>
        databaseService.ApplyAuthoritativeAttemptSummaryAsync(attemptId, status,
            lastAcceptedSequence, answerCount, submitted, serverRevision, cancellationToken);

    public Task<Examifo_Desktop.Domain.Models.Attempt?> GetAttemptAsync(Guid attemptId,
        CancellationToken cancellationToken = default) =>
        databaseService.GetAttemptAsync(attemptId, cancellationToken);

    public Task<Examifo_Desktop.Domain.Models.Submission?> GetSubmissionAsync(Guid attemptId,
        CancellationToken cancellationToken = default) =>
        databaseService.GetSubmissionAsync(attemptId, cancellationToken);

    public Task<List<Examifo_Desktop.Domain.Models.Submission>> GetSubmissionsAwaitingResultsAsync(
        CancellationToken cancellationToken = default) =>
        databaseService.GetSubmissionsAwaitingResultsAsync(cancellationToken);

    public Task<Examifo_Desktop.Domain.Models.Submission> ApplyResultAsync(Guid attemptId,
        string resultStatus, decimal? scoreTotal, decimal? scoreObtained, decimal? percentage,
        bool? passed, DateTime? submittedAtUtc, DateTime updatedAtUtc,
        CancellationToken cancellationToken = default) =>
        databaseService.ApplyAuthoritativeResultAsync(attemptId, resultStatus, scoreTotal,
            scoreObtained, percentage, passed, submittedAtUtc, updatedAtUtc, cancellationToken);
}
