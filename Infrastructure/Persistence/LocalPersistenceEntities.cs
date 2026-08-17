using SQLite;

namespace Examifo_Desktop.Infrastructure.Persistence;

public sealed class LocalUserRecord
{
    [PrimaryKey] public Guid UserId { get; set; }
    public string EncryptedName { get; set; } = string.Empty;
    public string? EncryptedEmail { get; set; }
    public DateTime LastOnlineLoginUtc { get; set; }
}

public sealed class LocalDeviceRecord
{
    [PrimaryKey] public Guid DeviceId { get; set; }
    [Indexed] public Guid InstallationId { get; set; }
    public string EncryptedName { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    [Indexed] public string Status { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class DownloadedExamRecord
{
    [PrimaryKey] public Guid ExamId { get; set; }
    public long PackageVersion { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public DateTime DownloadedAtUtc { get; set; }
    [Indexed] public string State { get; set; } = string.Empty;
}

public sealed class AvailableExamRecord
{
    [PrimaryKey] public string CacheKey { get; set; } = string.Empty;
    [Indexed] public Guid CandidateId { get; set; }
    [Indexed] public Guid ExamId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
    public int MaxAttempts { get; set; }
    public bool ProctoringEnabled { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime EndsAtUtc { get; set; }
    public long PackageVersion { get; set; }
    public string PackageHash { get; set; } = string.Empty;
    public long PackageSizeBytes { get; set; }
    public bool CanDownload { get; set; }
    public bool CanStartOffline { get; set; }
    public string? ExistingAttemptStatus { get; set; }
    public DateTime RefreshedAtUtc { get; set; }
}

public sealed class ExamCatalogueCheckpointRecord
{
    [PrimaryKey] public Guid CandidateId { get; set; }
    public DateTime LastServerRefreshUtc { get; set; }
}

public sealed class AttemptAuthorizationRecord
{
    [PrimaryKey] public Guid AuthorizationId { get; set; }
    [Indexed] public Guid AttemptId { get; set; }
    [Indexed] public Guid ExamId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid DeviceId { get; set; }
    public long PackageVersion { get; set; }
    public DateTime NotBeforeUtc { get; set; }
    public DateTime MustStartBeforeUtc { get; set; }
    public DateTime MustSubmitBeforeUtc { get; set; }
    public int? DurationSeconds { get; set; }
    public int AttemptNumber { get; set; }
    public string EncryptedShuffleSeed { get; set; } = string.Empty;
    public string EncryptedAuthorizationToken { get; set; } = string.Empty;
    public DateTime ServerTimeUtc { get; set; }
    [Indexed] public string State { get; set; } = "Authorized";
}

public sealed class SyncCheckpointRecord
{
    [PrimaryKey] public Guid ClientId { get; set; }
    public long LastServerRevision { get; set; }
    public DateTime? LastSuccessfulSyncUtc { get; set; }
    public string? PullCursor { get; set; }
}

public sealed class ProctoringEventRecord
{
    [PrimaryKey] public Guid EventId { get; set; } = Guid.NewGuid();
    [Indexed] public Guid AttemptId { get; set; }
    [Indexed] public string EventType { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string EncryptedMetadataJson { get; set; } = string.Empty;
    public long Sequence { get; set; }
    [Indexed] public Guid OperationId { get; set; }
}
