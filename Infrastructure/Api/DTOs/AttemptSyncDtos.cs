using System.Text.Json;

namespace Examifo_Desktop.Infrastructure.Api.DTOs;

public sealed record OfflineAuthorizationRequest(Guid DeviceId, long PackageVersion);
public sealed record OfflineAuthorizationResponse(
    Guid AuthorizationId, Guid AttemptId, Guid ExamId, Guid CandidateId, Guid DeviceId,
    long PackageVersion, DateTimeOffset NotBeforeUtc, DateTimeOffset MustStartBeforeUtc,
    DateTimeOffset MustSubmitBeforeUtc, int? DurationSeconds, int AttemptNumber,
    string ShuffleSeed, DateTimeOffset ServerTimeUtc, string AuthorizationToken);
public sealed record OfflineAuthorizationSummary(
    Guid AuthorizationId, Guid AttemptId, Guid ExamId, long PackageVersion,
    DateTimeOffset NotBeforeUtc, DateTimeOffset MustStartBeforeUtc,
    DateTimeOffset MustSubmitBeforeUtc, string Status,
    DateTimeOffset? StartedAtUtc, DateTimeOffset? ConsumedAtUtc);
public sealed record SyncPushRequest(Guid ClientId, Guid BatchId, List<SyncOperationRequest> Operations);
public sealed record SyncOperationRequest(
    Guid OperationId, Guid AttemptId, Guid AuthorizationId, long Sequence,
    string Type, DateTimeOffset OccurredAtUtc, long PackageVersion, JsonElement Payload);
public sealed record SyncPushResponse(Guid BatchId, DateTimeOffset ServerTimeUtc, List<SyncItemResult> Results);
public sealed record SyncItemResult(
    Guid OperationId, string Status, long? ServerRevision, string? ErrorCode, string? Message);
