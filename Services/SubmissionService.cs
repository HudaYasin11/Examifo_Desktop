using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Infrastructure.Sync;

namespace Examifo_Desktop.Services;

public sealed class SubmissionService(
    SubmissionApiClient apiClient,
    AuthenticationService authenticationService,
    OutboxService outboxService,
    TrustedServerTimeService trustedTime)
{
    public async Task SyncPendingAsync(CancellationToken cancellationToken = default)
    {
        await outboxService.RecoverStaleInFlightAsync(DateTime.UtcNow.AddMinutes(-2), cancellationToken);
        var pending = await outboxService.ClaimPendingAsync(cancellationToken: cancellationToken);
        if (pending.Count == 0) return;

        try
        {
            Guid deviceId = await authenticationService.GetDeviceIdAsync();
            var request = new SyncPushRequest(deviceId, Guid.NewGuid(), pending.Select(operation =>
                new SyncOperationRequest(
                    operation.OperationId,
                    operation.AttemptId,
                    operation.AuthorizationId,
                    operation.Sequence,
                    operation.Type,
                    new DateTimeOffset(DateTime.SpecifyKind(operation.OccurredAtUtc, DateTimeKind.Utc)),
                    operation.PackageVersion,
                    JsonSerializer.Deserialize<JsonElement>(operation.PayloadJson))).ToList());
            DateTimeOffset requestStarted = DateTimeOffset.UtcNow;
            SyncPushResponse response = await apiClient.PushAsync(request, cancellationToken);
            trustedTime.RecordSample(response.ServerTimeUtc, requestStarted, DateTimeOffset.UtcNow);
            var completed = new HashSet<Guid>();
            foreach (SyncItemResult result in response.Results)
            {
                string state = result.Status.ToLowerInvariant() switch
                {
                    "accepted" => "Accepted",
                    "duplicate" => "Duplicate",
                    "retry_later" or "retrylater" => "RetryLater",
                    _ => "Rejected"
                };
                await outboxService.MarkResultAsync(
                    result.OperationId, state, result.ErrorCode, result.ServerRevision, cancellationToken);
                completed.Add(result.OperationId);
            }
            long? highestServerRevision = response.Results
                .Where(x => x.ServerRevision.HasValue)
                .Select(x => x.ServerRevision)
                .Max();
            if (highestServerRevision.HasValue)
                await outboxService.AdvanceCheckpointAsync(deviceId, highestServerRevision.Value,
                    DateTime.UtcNow, cancellationToken: cancellationToken);
            Guid[] uncertain = pending.Select(x => x.OperationId).Where(x => !completed.Contains(x)).ToArray();
            if (uncertain.Length > 0)
                await outboxService.ReturnForRetryAsync(uncertain, DateTime.UtcNow.AddSeconds(5), cancellationToken);
        }
        catch
        {
            await outboxService.ReturnForRetryAsync(
                pending.Select(x => x.OperationId), DateTime.UtcNow.AddSeconds(5), CancellationToken.None);
            throw;
        }
    }
}
