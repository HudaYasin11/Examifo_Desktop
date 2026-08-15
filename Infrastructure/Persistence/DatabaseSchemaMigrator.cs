using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Sync;
using SQLite;

namespace Examifo_Desktop.Infrastructure.Persistence;

public sealed class SchemaMigrationRecord
{
    [PrimaryKey]
    public int Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime AppliedAtUtc { get; set; }
}

public sealed class DatabaseMigrationException(int targetVersion, Exception innerException)
    : Exception($"The local Examifo database could not be migrated to schema version {targetVersion}.", innerException)
{
    public int TargetVersion { get; } = targetVersion;
}

public sealed class DatabaseSchemaMigrator(SQLiteAsyncConnection database)
{
    public const int CurrentVersion = 4;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            await database.CreateTableAsync<SchemaMigrationRecord>();
            int installedVersion = await GetInstalledVersionAsync();
            if (installedVersion > CurrentVersion)
                throw new DatabaseMigrationException(installedVersion,
                    new InvalidOperationException("The local database was created by a newer Examifo version."));

            for (int targetVersion = installedVersion + 1; targetVersion <= CurrentVersion; targetVersion++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ApplyMigrationAsync(targetVersion);
                }
                catch (Exception ex) when (ex is not DatabaseMigrationException)
                {
                    throw new DatabaseMigrationException(targetVersion, ex);
                }
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetInstalledVersionAsync()
    {
        SchemaMigrationRecord? latest = await database.Table<SchemaMigrationRecord>()
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync();
        return latest?.Version ?? 0;
    }

    private Task ApplyMigrationAsync(int version) => version switch
    {
        1 => database.RunInTransactionAsync(connection =>
        {
            connection.CreateTable<Attempt>();
            connection.CreateTable<Answer>();
            connection.CreateTable<Submission>();
            connection.CreateTable<SyncOperation>();
            connection.Execute(
                "CREATE UNIQUE INDEX IF NOT EXISTS IX_Outbox_Attempt_Sequence ON SyncOperation (AttemptId, Sequence)");
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 1,
                Name = "Initial durable attempt and outbox schema",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        2 => database.RunInTransactionAsync(connection =>
        {
            connection.CreateTable<LocalUserRecord>();
            connection.CreateTable<LocalDeviceRecord>();
            connection.CreateTable<DownloadedExamRecord>();
            connection.CreateTable<AttemptAuthorizationRecord>();
            connection.CreateTable<SyncCheckpointRecord>();
            connection.CreateTable<ProctoringEventRecord>();

            // Preserve the newest legacy row before enforcing logical uniqueness.
            connection.Execute("DELETE FROM Answer WHERE rowid NOT IN " +
                "(SELECT MAX(rowid) FROM Answer GROUP BY AttemptId, QuestionId)");
            connection.Execute("DELETE FROM Submission WHERE rowid NOT IN " +
                "(SELECT MAX(rowid) FROM Submission GROUP BY AttemptId)");
            connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_Answer_Attempt_Question " +
                "ON Answer (AttemptId, QuestionId)");
            connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_Submission_Attempt " +
                "ON Submission (AttemptId)");
            connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_Authorization_Attempt " +
                "ON AttemptAuthorizationRecord (AttemptId)");
            connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_Authorization_Exam " +
                "ON AttemptAuthorizationRecord (ExamId)");

            connection.Insert(new SchemaMigrationRecord
            {
                Version = 2,
                Name = "Complete local preservation entities and constraints",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        3 => database.RunInTransactionAsync(connection =>
        {
            connection.CreateTable<Attempt>();
            connection.CreateTable<Answer>();
            connection.CreateTable<SyncOperation>();
            connection.Execute("UPDATE Answer SET ResponseFormat = 'selected_options' " +
                "WHERE ResponseFormat IS NULL OR ResponseFormat = ''");
            connection.Execute("UPDATE Answer SET Revision = 1 WHERE Revision < 1");
            connection.Execute("UPDATE Attempt SET LastActivityUtc = StartedAtUtc " +
                "WHERE LastActivityUtc = 0");
            connection.Execute("CREATE INDEX IF NOT EXISTS IX_Outbox_State_NextAttempt " +
                "ON SyncOperation (State, NextAttemptAtUtc)");
            connection.Execute("CREATE INDEX IF NOT EXISTS IX_Attempt_Status_LastActivity " +
                "ON Attempt (Status, LastActivityUtc)");
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 3,
                Name = "Generalized answers, recovery state, and durable outbox lifecycle",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        4 => database.RunInTransactionAsync(connection =>
        {
            connection.CreateTable<ProctoringEventRecord>();
            connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_Proctoring_Operation " +
                "ON ProctoringEventRecord (OperationId) " +
                "WHERE OperationId <> '00000000-0000-0000-0000-000000000000'");
            connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_Proctoring_Attempt_Sequence " +
                "ON ProctoringEventRecord (AttemptId, Sequence) WHERE Sequence > 0");
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 4,
                Name = "Checkpoint, proctoring outbox, and integrity hardening",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        _ => throw new InvalidOperationException($"No database migration exists for version {version}.")
    };
}
