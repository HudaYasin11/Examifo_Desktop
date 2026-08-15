using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Services;

public sealed class SubmissionService(
    SubmissionApiClient apiClient,
    AuthenticationService authenticationService,
    DatabaseService databaseService)
{
    public async Task SyncPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await databaseService.GetPendingOperationsAsync();
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

        SyncPushResponse response = await apiClient.PushAsync(request, cancellationToken);
        foreach (SyncItemResult result in response.Results)
        {
            string state = result.Status is "accepted" or "duplicate" ? "Accepted" : "Rejected";
            await databaseService.ApplySyncResultAsync(
                result.OperationId, state, result.ErrorCode, result.ServerRevision);
        }
    }
}
