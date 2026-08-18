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
    public const int CurrentVersion = 11;
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
        5 => database.RunInTransactionAsync(connection =>
        {
            connection.CreateTable<AvailableExamRecord>();
            connection.CreateTable<ExamCatalogueCheckpointRecord>();
            connection.Execute("CREATE INDEX IF NOT EXISTS IX_AvailableExam_Refresh " +
                "ON AvailableExamRecord (RefreshedAtUtc)");
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 5,
                Name = "Durable available-exam catalogue cache",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        6 => database.RunInTransactionAsync(connection =>
        {
            int exists = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM pragma_table_info('Attempt') WHERE name = 'EncryptedShuffleSeed'");
            if (exists == 0)
                connection.Execute("ALTER TABLE Attempt ADD COLUMN EncryptedShuffleSeed varchar NOT NULL DEFAULT ''");
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 6,
                Name = "Durable encrypted deterministic exam ordering seed",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        7 => database.RunInTransactionAsync(connection =>
        {
            int exists = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM pragma_table_info('Attempt') WHERE name = 'CandidateId'");
            if (exists == 0)
                connection.Execute("ALTER TABLE Attempt ADD COLUMN CandidateId varchar(36) " +
                    "NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");

            // Authorization rows may still exist for attempts created by an older build. Use
            // them to recover ownership where possible; unmatched legacy attempts remain
            // unowned and can only be claimed after their device is authenticated again.
            connection.Execute("UPDATE Attempt SET CandidateId = (" +
                "SELECT CandidateId FROM AttemptAuthorizationRecord a " +
                "WHERE a.AttemptId = Attempt.Id LIMIT 1) " +
                "WHERE CandidateId = '00000000-0000-0000-0000-000000000000' " +
                "AND EXISTS (SELECT 1 FROM AttemptAuthorizationRecord a WHERE a.AttemptId = Attempt.Id)");
            connection.Execute("CREATE INDEX IF NOT EXISTS IX_Attempt_Candidate_Exam_Status " +
                "ON Attempt (CandidateId, ExamId, Status)");

            // Offline authorizations are candidate-owned. The former ExamId-only uniqueness
            // prevented two candidates on one installation from holding the same exam.
            connection.Execute("DROP INDEX IF EXISTS IX_Authorization_Exam");
            connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_Authorization_Candidate_Exam " +
                "ON AttemptAuthorizationRecord (CandidateId, ExamId)");
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 7,
                Name = "Candidate-isolated attempts and offline authorizations",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        8 => database.RunInTransactionAsync(connection =>
        {
            // The former catalogue and checkpoint were shared by every account on the
            // installation. They cannot be attributed safely, so discard only this
            // reconstructible cache and recreate it with candidate ownership.
            connection.Execute("DROP TABLE IF EXISTS AvailableExamRecord");
            connection.Execute("DROP TABLE IF EXISTS ExamCatalogueCheckpointRecord");
            connection.CreateTable<AvailableExamRecord>();
            connection.CreateTable<ExamCatalogueCheckpointRecord>();
            connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS IX_AvailableExam_Candidate_Exam " +
                "ON AvailableExamRecord (CandidateId, ExamId)");
            connection.Execute("CREATE INDEX IF NOT EXISTS IX_AvailableExam_Candidate_Title " +
                "ON AvailableExamRecord (CandidateId, Title)");
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 8,
                Name = "Candidate-isolated authoritative exam catalogue",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        9 => database.RunInTransactionAsync(connection =>
        {
            int maxAttemptsExists = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM pragma_table_info('AvailableExamRecord') WHERE name = 'MaxAttempts'");
            if (maxAttemptsExists == 0)
                connection.Execute("ALTER TABLE AvailableExamRecord ADD COLUMN MaxAttempts integer NOT NULL DEFAULT 0");
            int proctoringExists = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM pragma_table_info('AvailableExamRecord') WHERE name = 'ProctoringEnabled'");
            if (proctoringExists == 0)
                connection.Execute("ALTER TABLE AvailableExamRecord ADD COLUMN ProctoringEnabled integer NOT NULL DEFAULT 0");
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 9,
                Name = "Authoritative exam metadata cache",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        10 => database.RunInTransactionAsync(connection =>
        {
            connection.CreateTable<Submission>();
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 10,
                Name = "Authoritative grading and result state",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        11 => database.RunInTransactionAsync(connection =>
        {
            connection.Execute("UPDATE SyncOperation SET NextAttemptAtUtc = NULL " +
                "WHERE State IN ('Accepted', 'Duplicate', 'Rejected')");
            connection.Insert(new SchemaMigrationRecord
            {
                Version = 11,
                Name = "Clear obsolete terminal-operation retry schedules",
                AppliedAtUtc = DateTime.UtcNow
            });
        }),
        _ => throw new InvalidOperationException($"No database migration exists for version {version}.")
    };
}
