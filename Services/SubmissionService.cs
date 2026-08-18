using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Infrastructure.Sync;

namespace Examifo_Desktop.Services;

public sealed class SubmissionService(
    SubmissionApiClient apiClient,
    AuthenticationService authenticationService,
    OutboxService outboxService,
    TrustedServerTimeService trustedTime) : ISubmissionSynchronizer
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public async Task SyncPendingAsync(CancellationToken cancellationToken = default)
    {
        await _syncGate.WaitAsync(cancellationToken);
        try
        {
            DateTime staleBeforeUtc = DateTime.UtcNow.AddMinutes(-2);
            await ResolveUncertainOperationsAsync(staleBeforeUtc, cancellationToken);
            await outboxService.RecoverStaleInFlightAsync(staleBeforeUtc, cancellationToken);
            Guid deviceId = await authenticationService.GetDeviceIdAsync();
            var pending = await outboxService.ClaimPendingAsync(cancellationToken: cancellationToken);
            if (pending.Count > 0)
            {
                try
                {
                    var request = new SyncPushRequest(deviceId, Guid.NewGuid(), pending.Select(operation =>
                    {
                        JsonElement payload = JsonSerializer.Deserialize<JsonElement>(operation.PayloadJson);
                        if (payload.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException($"Sync operation {operation.OperationId} has a non-object payload.");
                        return new SyncOperationRequest(
                            operation.OperationId, operation.AttemptId, operation.AuthorizationId,
                            operation.Sequence, operation.Type,
                            new DateTimeOffset(DateTime.SpecifyKind(operation.OccurredAtUtc, DateTimeKind.Utc)),
                            operation.PackageVersion, payload);
                    }).ToList());
                    DateTimeOffset requestStarted = DateTimeOffset.UtcNow;
                    SyncPushResponse response = await apiClient.PushAsync(request, cancellationToken);
                    ValidateResponse(request, response);
                    trustedTime.RecordSample(response.ServerTimeUtc, requestStarted, DateTimeOffset.UtcNow);
                    var completed = new HashSet<Guid>();
                    foreach (SyncItemResult result in response.Results)
                    {
                        await outboxService.MarkResultAsync(result.OperationId, NormalizeState(result.Status),
                            result.ErrorCode, result.ServerRevision, cancellationToken);
                        completed.Add(result.OperationId);
                    }
                    long? highestServerRevision = response.Results.Where(x => x.ServerRevision.HasValue)
                        .Select(x => x.ServerRevision).Max();
                    if (highestServerRevision.HasValue)
                        await outboxService.AdvanceCheckpointAsync(deviceId, highestServerRevision.Value,
                            DateTime.UtcNow, cancellationToken: cancellationToken);
                    Guid[] uncertain = pending.Select(x => x.OperationId)
                        .Where(x => !completed.Contains(x)).ToArray();
                    if (uncertain.Length > 0)
                        await ScheduleRetriesAsync(pending.Where(x => uncertain.Contains(x.OperationId)), cancellationToken);
                }
                catch
                {
                    await ScheduleRetriesAsync(pending, CancellationToken.None);
                    throw;
                }
            }
            await PullChangesAsync(deviceId, cancellationToken);
            await ReconcileAttemptsAsync(cancellationToken);
            await RefreshPendingResultsSafelyAsync(cancellationToken);
        }
        finally { _syncGate.Release(); }
    }

    private async Task ResolveUncertainOperationsAsync(DateTime staleBeforeUtc,
        CancellationToken cancellationToken)
    {
        foreach (SyncOperation operation in await outboxService.GetStaleInFlightAsync(
            staleBeforeUtc, cancellationToken))
        {
            try
            {
                SyncOperationStatusResponse status = await apiClient.GetOperationStatusAsync(
                    operation.OperationId, cancellationToken);
                if (status.OperationId != operation.OperationId || status.ReceivedAtUtc == default)
                    throw new InvalidDataException("Examifo returned an inconsistent operation-status response.");
                if (status.Status.ToLowerInvariant() is "accepted" or "duplicate" or "rejected"
                    or "retry_later" or "retrylater")
                    await outboxService.MarkResultAsync(operation.OperationId, NormalizeState(status.Status),
                        status.ErrorCode, status.ServerRevision, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // The original idempotent operation is returned for retry below. Never create a replacement.
            }
        }
    }

    private async Task ReconcileAttemptsAsync(CancellationToken cancellationToken)
    {
        foreach (var local in await outboxService.GetAttemptsRequiringRecoveryAsync(cancellationToken))
        {
            AttemptRecoveryResponse attempt = await apiClient.GetAttemptAsync(local.Id, cancellationToken);
            AttemptSyncSummaryResponse summary = await apiClient.GetAttemptSyncSummaryAsync(local.Id, cancellationToken);
            if (attempt.Id != local.Id || attempt.ExamId != local.ExamId || summary.AttemptId != local.Id
                || !string.Equals(attempt.Status, summary.Status, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Examifo returned inconsistent authoritative attempt recovery data.");
            await outboxService.ApplyAttemptSummaryAsync(local.Id, summary.Status,
                summary.LastAcceptedSequence, summary.AnswerCount, summary.Submitted,
                summary.ServerRevision, cancellationToken);
        }
    }

    private static string NormalizeState(string status) => status.ToLowerInvariant() switch
    {
        "accepted" => OutboxStates.Accepted,
        "duplicate" => OutboxStates.Duplicate,
        "retry_later" or "retrylater" => OutboxStates.RetryLater,
        "rejected" => OutboxStates.Rejected,
        _ => throw new InvalidDataException($"Examifo returned an unknown synchronization status '{status}'.")
    };

    public async Task<Domain.Models.Submission> RefreshResultAsync(Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        Domain.Models.Attempt attempt = await outboxService.GetAttemptAsync(attemptId, cancellationToken)
            ?? throw new InvalidOperationException("The local attempt no longer exists.");
        if (attempt.Status != Domain.Enums.AttemptStatus.Synced)
            return await outboxService.GetSubmissionAsync(attemptId, cancellationToken)
                ?? throw new InvalidOperationException("The local submission no longer exists.");
        AttemptResultResponse response = await apiClient.GetAttemptResultAsync(attemptId, cancellationToken);
        DateTime? submittedAtUtc = response.SubmittedAt?.UtcDateTime;
        return await outboxService.ApplyResultAsync(attemptId, response.Status,
            response.ScoreTotal, response.ScoreObtained, response.Percentage, response.Passed,
            submittedAtUtc, DateTime.UtcNow, cancellationToken);
    }

    public async Task<Domain.Models.Submission> PollResultAsync(Guid attemptId, int maximumPolls = 5,
        CancellationToken cancellationToken = default)
    {
        if (maximumPolls is <= 0 or > 10) throw new ArgumentOutOfRangeException(nameof(maximumPolls));
        Domain.Models.Submission result = await RefreshResultAsync(attemptId, cancellationToken);
        for (int poll = 1; poll < maximumPolls && result.ResultStatus == "grading"; poll++)
        {
            TimeSpan delay = ResultPollingPolicy.GetDelay(poll - 1);
            await Task.Delay(delay, cancellationToken);
            result = await RefreshResultAsync(attemptId, cancellationToken);
        }
        return result;
    }

    private async Task RefreshPendingResultsSafelyAsync(CancellationToken cancellationToken)
    {
        foreach (Domain.Models.Submission submission in
            await outboxService.GetSubmissionsAwaitingResultsAsync(cancellationToken))
        {
            try { await RefreshResultAsync(submission.AttemptId, cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Result availability is independent of the already-successful submission sync.
            }
        }
    }

    private async Task ScheduleRetriesAsync(IEnumerable<SyncOperation> operations,
        CancellationToken cancellationToken)
    {
        DateTime now = DateTime.UtcNow;
        foreach (SyncOperation operation in operations)
        {
            TimeSpan delay = SyncRetryPolicy.GetDelay(operation.RetryCount, Random.Shared.NextDouble());
            await outboxService.ReturnForRetryAsync([operation.OperationId], now.Add(delay), cancellationToken);
        }
    }

    private static void ValidateResponse(SyncPushRequest request, SyncPushResponse response)
    {
        if (response.BatchId != request.BatchId)
            throw new InvalidDataException("Examifo returned a synchronization response for a different batch.");
        if (response.ServerTimeUtc == default || response.Results is null)
            throw new InvalidDataException("Examifo returned an incomplete synchronization response.");
        HashSet<Guid> requested = request.Operations.Select(x => x.OperationId).ToHashSet();
        var returned = new HashSet<Guid>();
        foreach (SyncItemResult result in response.Results)
        {
            if (!requested.Contains(result.OperationId) || !returned.Add(result.OperationId))
                throw new InvalidDataException("Examifo returned an unknown or duplicate synchronization result.");
            if (result.Status.ToLowerInvariant() is not
                ("accepted" or "duplicate" or "rejected" or "retry_later" or "retrylater"))
                throw new InvalidDataException($"Examifo returned an unknown synchronization status '{result.Status}'.");
        }
    }

    private async Task PullChangesAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        const int maximumPages = 100;
        for (int page = 0; page < maximumPages; page++)
        {
            var checkpoint = await outboxService.GetCheckpointAsync(deviceId, cancellationToken);
            long afterRevision = checkpoint?.LastServerRevision ?? 0;
            SyncPullResponse response = await apiClient.PullAsync(deviceId, afterRevision, cancellationToken);
            if (response.Changes is null || response.NextRevision < afterRevision)
                throw new InvalidDataException("Examifo returned an invalid synchronization pull response.");
            if (response.HasMore && response.NextRevision <= afterRevision)
                throw new InvalidDataException("Examifo reported more synchronization changes without advancing its revision.");
            PulledSyncChange[] changes = response.Changes.Select(change => new PulledSyncChange(
                change.Revision, change.Type, change.EntityId, change.Payload.Clone())).ToArray();
            await outboxService.ApplyPulledChangesAsync(
                deviceId, response.NextRevision, changes, DateTime.UtcNow, cancellationToken);
            if (!response.HasMore) return;
        }
        throw new InvalidDataException("Examifo returned too many synchronization pull pages.");
    }
}
