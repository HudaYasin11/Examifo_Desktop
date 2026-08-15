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
}
