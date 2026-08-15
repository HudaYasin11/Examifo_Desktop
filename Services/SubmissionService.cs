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
        var pending = await outboxService.GetPendingAsync(cancellationToken: cancellationToken);
        if (pending.Count == 0) return;

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
        foreach (SyncItemResult result in response.Results)
        {
            string state = result.Status is "accepted" or "duplicate" ? "Accepted" : "Rejected";
            await outboxService.MarkResultAsync(
                result.OperationId, state, result.ErrorCode, result.ServerRevision, cancellationToken);
        }
    }
}
