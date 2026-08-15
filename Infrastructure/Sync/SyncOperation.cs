using SQLite;

namespace Examifo_Desktop.Infrastructure.Sync;

public sealed class SyncOperation
{
    [PrimaryKey]
    public Guid OperationId { get; set; } = Guid.NewGuid();
    [Indexed]
    public Guid AttemptId { get; set; }
    public Guid AuthorizationId { get; set; }
    public long Sequence { get; set; }
    public string Type { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public long PackageVersion { get; set; }
    public string PayloadJson { get; set; } = "{}";
    [Indexed]
    public string State { get; set; } = "Pending";
    public string? ErrorCode { get; set; }
    public long? ServerRevision { get; set; }
    public int RetryCount { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? InFlightAtUtc { get; set; }
}

public static class OutboxStates
{
    public const string Pending = "Pending";
    public const string InFlight = "InFlight";
    public const string Accepted = "Accepted";
    public const string Duplicate = "Duplicate";
    public const string Rejected = "Rejected";
    public const string RetryLater = "RetryLater";
}
