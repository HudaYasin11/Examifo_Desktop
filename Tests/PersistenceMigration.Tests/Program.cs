using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Persistence;
using Examifo_Desktop.Infrastructure.Security;
using Examifo_Desktop.Services;
using SQLite;
using System.Text.Json;

string testDirectory = Path.Combine(Path.GetTempPath(), "examifo-persistence-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);

try
{
    await TestFreshDatabaseAsync(Path.Combine(testDirectory, "fresh.db3"));
    await TestRepeatedAndConcurrentInitializationAsync(Path.Combine(testDirectory, "repeated.db3"));
    await TestLegacyDataSurvivesUpgradeAsync(Path.Combine(testDirectory, "legacy.db3"));
    await TestNewerSchemaIsRejectedAsync(Path.Combine(testDirectory, "newer.db3"));
    await TestSensitiveFieldsAreEncryptedAsync(Path.Combine(testDirectory, "encrypted.db3"));
    await TestLogicalUniquenessConstraintsAsync(Path.Combine(testDirectory, "constraints.db3"));
    await TestDurableAttemptLifecycleAsync(Path.Combine(testDirectory, "lifecycle.db3"));
    await TestCandidateAttemptIsolationAsync(Path.Combine(testDirectory, "candidate-isolation.db3"));
    await TestCheckpointAndProctoringAsync(Path.Combine(testDirectory, "checkpoint.db3"));
    await TestIntegrityFailurePreservesDataAsync(Path.Combine(testDirectory, "integrity.db3"));
    await TestCrashAndConcurrencySafetyAsync(Path.Combine(testDirectory, "concurrency.db3"));
    await TestCorruptEncryptionKeyIsNotReplacedAsync();
    await TestDurableIncrementalCatalogueAsync(Path.Combine(testDirectory, "catalogue.db3"));
    await TestDownloadedPackageActivationAsync(Path.Combine(testDirectory, "packages.db3"));
    Console.WriteLine("All persistence migration tests passed.");
}
finally
{
    try { Directory.Delete(testDirectory, recursive: true); }
    catch (IOException) { /* SQLite may release native handles shortly after CloseAsync on Windows. */ }
}

static async Task TestFreshDatabaseAsync(string path)
{
    var connection = new SQLiteAsyncConnection(path);
    var migrator = new DatabaseSchemaMigrator(connection);
    await migrator.InitializeAsync();

    Assert(await migrator.GetInstalledVersionAsync() == DatabaseSchemaMigrator.CurrentVersion,
        "fresh database reaches the current schema version");
    Assert(await TableExistsAsync(connection, "Attempt") && await TableExistsAsync(connection, "Answer")
        && await TableExistsAsync(connection, "Submission") && await TableExistsAsync(connection, "SyncOperation")
        && await TableExistsAsync(connection, "LocalUserRecord")
        && await TableExistsAsync(connection, "LocalDeviceRecord")
        && await TableExistsAsync(connection, "DownloadedExamRecord")
        && await TableExistsAsync(connection, "AttemptAuthorizationRecord")
        && await TableExistsAsync(connection, "SyncCheckpointRecord")
        && await TableExistsAsync(connection, "ProctoringEventRecord"),
        "fresh migration creates every required preservation table");
    Assert(await TableExistsAsync(connection, "AvailableExamRecord")
        && await TableExistsAsync(connection, "ExamCatalogueCheckpointRecord"),
        "fresh migration creates the durable exam catalogue cache");
    await connection.CloseAsync();
}

static async Task TestRepeatedAndConcurrentInitializationAsync(string path)
{
    var connection = new SQLiteAsyncConnection(path);
    var migrator = new DatabaseSchemaMigrator(connection);
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => migrator.InitializeAsync()));
    await migrator.InitializeAsync();

    int records = await connection.Table<SchemaMigrationRecord>().CountAsync();
    Assert(records == DatabaseSchemaMigrator.CurrentVersion,
        "repeated and concurrent initialization applies each migration once");
    await connection.CloseAsync();
}

static async Task TestLegacyDataSurvivesUpgradeAsync(string path)
{
    var connection = new SQLiteAsyncConnection(path);
    await connection.CreateTableAsync<Attempt>();
    var original = new Attempt { Id = Guid.NewGuid(), ExamId = Guid.NewGuid(), NextSequence = 7 };
    await connection.InsertAsync(original);

    var migrator = new DatabaseSchemaMigrator(connection);
    await migrator.InitializeAsync();
    Attempt? restored = await connection.FindAsync<Attempt>(original.Id);

    Assert(restored is not null && restored.ExamId == original.ExamId && restored.NextSequence == 7,
        "migration preserves rows created by the legacy initializer");
    Assert(await migrator.GetInstalledVersionAsync() == DatabaseSchemaMigrator.CurrentVersion,
        "legacy database receives durable migration history");
    await connection.CloseAsync();
}

static async Task TestNewerSchemaIsRejectedAsync(string path)
{
    var connection = new SQLiteAsyncConnection(path);
    await connection.CreateTableAsync<SchemaMigrationRecord>();
    await connection.InsertAsync(new SchemaMigrationRecord
    {
        Version = DatabaseSchemaMigrator.CurrentVersion + 1,
        Name = "Future schema",
        AppliedAtUtc = DateTime.UtcNow
    });

    try
    {
        await new DatabaseSchemaMigrator(connection).InitializeAsync();
        throw new InvalidOperationException("FAIL: newer database schema is rejected without downgrade");
    }
    catch (DatabaseMigrationException ex)
        when (ex.TargetVersion == DatabaseSchemaMigrator.CurrentVersion + 1)
    {
        Console.WriteLine("PASS: newer database schema is rejected without downgrade");
    }
    await connection.CloseAsync();
}

static async Task TestSensitiveFieldsAreEncryptedAsync(string path)
{
    var secureStore = new MemorySecureValueStore();
    var service = new DatabaseService(new EncryptionService(secureStore), new TestPathProvider(path));
    Guid userId = Guid.NewGuid();
    Guid deviceId = Guid.NewGuid();
    Guid installationId = Guid.NewGuid();
    Guid attemptId = Guid.NewGuid();
    Guid authorizationId = Guid.NewGuid();
    Guid examId = Guid.NewGuid();
    const string candidateName = "Sensitive Candidate Name";
    const string candidateEmail = "candidate@example.test";
    const string deviceName = "Private Laptop Name";
    const string token = "one-time-secret-token";
    const string shuffleSeed = "secret-shuffle-seed";
    const string metadata = "{\"windowTitle\":\"Private notes\"}";

    await service.SaveLocalUserAsync(userId, candidateName, candidateEmail, DateTime.UtcNow);
    await service.SaveLocalDeviceAsync(new LocalDeviceRecord
    {
        DeviceId = deviceId, InstallationId = installationId, EncryptedName = deviceName,
        Platform = "Windows", AppVersion = "1.0", Status = "Active", UpdatedAtUtc = DateTime.UtcNow
    });
    await service.SaveAttemptAuthorizationAsync(new AttemptAuthorizationRecord
    {
        AuthorizationId = authorizationId, AttemptId = attemptId, ExamId = examId,
        CandidateId = userId, DeviceId = deviceId, PackageVersion = 1,
        NotBeforeUtc = DateTime.UtcNow, MustStartBeforeUtc = DateTime.UtcNow.AddHours(1),
        MustSubmitBeforeUtc = DateTime.UtcNow.AddHours(2), AttemptNumber = 1,
        ServerTimeUtc = DateTime.UtcNow
    }, shuffleSeed, token);
    var activeAttempt = new Attempt
    {
        Id = attemptId, ExamId = examId, AuthorizationId = authorizationId, DeviceId = deviceId,
        PackageVersion = 1, Status = Examifo_Desktop.Domain.Enums.AttemptStatus.InProgress,
        StartedAtUtc = DateTime.UtcNow, DeadlineUtc = DateTime.UtcNow.AddHours(1), NextSequence = 1
    };
    await service.StartAuthorizedAttemptAsync(activeAttempt, token);
    await service.RecordProctoringEventWithOperationAsync(
        attemptId, "window.changed", DateTime.UtcNow, metadata);

    var raw = new SQLiteAsyncConnection(path);
    LocalUserRecord user = (await raw.Table<LocalUserRecord>().ToListAsync()).Single();
    LocalDeviceRecord device = (await raw.Table<LocalDeviceRecord>().ToListAsync()).Single();
    AttemptAuthorizationRecord authorization =
        (await raw.Table<AttemptAuthorizationRecord>().ToListAsync()).Single();
    ProctoringEventRecord proctoring = (await raw.Table<ProctoringEventRecord>().ToListAsync()).Single();

    string combined = string.Join('|', user.EncryptedName, user.EncryptedEmail, device.EncryptedName,
        authorization.EncryptedAuthorizationToken, authorization.EncryptedShuffleSeed,
        proctoring.EncryptedMetadataJson);
    Assert(!combined.Contains(candidateName) && !combined.Contains(candidateEmail)
        && !combined.Contains(deviceName) && !combined.Contains(token)
        && !combined.Contains(shuffleSeed) && !combined.Contains("Private notes"),
        "raw SQLite rows do not expose protected plaintext");
    Assert(new[] { user.EncryptedName, user.EncryptedEmail!, device.EncryptedName,
            authorization.EncryptedAuthorizationToken, authorization.EncryptedShuffleSeed,
            proctoring.EncryptedMetadataJson }.All(value => value.StartsWith("enc:v1:")),
        "all sensitive persistence fields use versioned AES-GCM envelopes");
    await raw.CloseAsync();

    await service.RemoveAttemptAuthorizationAsync(authorizationId);
    var afterRemoval = new SQLiteAsyncConnection(path);
    Assert(await afterRemoval.Table<AttemptAuthorizationRecord>().CountAsync() == 0,
        "consumed authorization removes its encrypted one-time token");
    Guid legacyAttemptId = Guid.NewGuid();
    await afterRemoval.InsertAsync(new Attempt
    {
        Id = legacyAttemptId, ExamId = Guid.NewGuid(), Status = Examifo_Desktop.Domain.Enums.AttemptStatus.InProgress,
        StartedAtUtc = DateTime.UtcNow, DeadlineUtc = DateTime.UtcNow.AddHours(1), NextSequence = 1
    });
    await afterRemoval.InsertAsync(new Answer
    {
        AttemptId = legacyAttemptId, QuestionId = Guid.NewGuid(), ExamQuestionId = Guid.NewGuid(),
        Response = "legacy plaintext answer"
    });
    await afterRemoval.CloseAsync();

    var restartedService = new DatabaseService(new EncryptionService(secureStore), new TestPathProvider(path));
    await restartedService.InitializeAsync();
    var afterProtection = new SQLiteAsyncConnection(path);
    Answer protectedLegacy = (await afterProtection.Table<Answer>().ToListAsync()).Single();
    Assert(protectedLegacy.Response.StartsWith("enc:v1:")
        && !protectedLegacy.Response.Contains("legacy plaintext answer"),
        "startup converts legacy plaintext answers to encrypted values");
    await afterProtection.CloseAsync();
}

static async Task TestCheckpointAndProctoringAsync(string path)
{
    var store = new MemorySecureValueStore();
    var service = new DatabaseService(new EncryptionService(store), new TestPathProvider(path));
    Guid clientId = Guid.NewGuid();
    DateTime first = DateTime.UtcNow.AddMinutes(-1);
    DateTime second = DateTime.UtcNow;
    await service.AdvanceSyncCheckpointAsync(clientId, 20, first, "cursor-20");
    await service.AdvanceSyncCheckpointAsync(clientId, 12, second);
    SyncCheckpointRecord checkpoint = await service.GetSyncCheckpointAsync(clientId)
        ?? throw new InvalidOperationException("FAIL: checkpoint exists");
    Assert(checkpoint.LastServerRevision == 20 && checkpoint.LastSuccessfulSyncUtc == second
        && checkpoint.PullCursor == "cursor-20", "checkpoint advances monotonically without losing its pull cursor");

    var attempt = new Attempt
    {
        Id = Guid.NewGuid(), ExamId = Guid.NewGuid(), AuthorizationId = Guid.NewGuid(),
        DeviceId = clientId, PackageVersion = 2,
        Status = Examifo_Desktop.Domain.Enums.AttemptStatus.InProgress,
        StartedAtUtc = DateTime.UtcNow, DeadlineUtc = DateTime.UtcNow.AddHours(1), NextSequence = 1
    };
    await service.StartAuthorizedAttemptAsync(attempt, "token");
    await service.RecordProctoringEventWithOperationAsync(
        attempt.Id, "exam.view.hidden", DateTime.UtcNow, "{\"reason\":\"switch\"}");
    var raw = new SQLiteAsyncConnection(path);
    ProctoringEventRecord record = (await raw.Table<ProctoringEventRecord>().ToListAsync()).Single();
    var operation = (await raw.Table<Examifo_Desktop.Infrastructure.Sync.SyncOperation>().ToListAsync())
        .Single(x => x.Type == "proctoring.event-recorded");
    Assert(record.OperationId == operation.OperationId && record.Sequence == operation.Sequence
        && record.EncryptedMetadataJson.StartsWith("enc:v1:") && operation.PayloadJson.StartsWith("enc:v1:"),
        "proctoring event and encrypted outbox operation commit atomically at one sequence");
    await raw.CloseAsync();
}

static async Task TestIntegrityFailurePreservesDataAsync(string path)
{
    var service = new DatabaseService(new EncryptionService(new MemorySecureValueStore()),
        new TestPathProvider(path));
    await service.InitializeAsync();
    var raw = new SQLiteAsyncConnection(path);
    Guid orphanId = Guid.NewGuid();
    await raw.InsertAsync(new Answer
    {
        Id = orphanId, AttemptId = Guid.NewGuid(), QuestionId = Guid.NewGuid(),
        ExamQuestionId = Guid.NewGuid(), Response = "enc:v1:preserved"
    });
    await raw.CloseAsync();

    var restarted = new DatabaseService(new EncryptionService(new MemorySecureValueStore()),
        new TestPathProvider(path));
    bool rejected = false;
    try { await restarted.InitializeAsync(); }
    catch (DatabaseIntegrityException) { rejected = true; }
    var verify = new SQLiteAsyncConnection(path);
    Assert(rejected && await verify.FindAsync<Answer>(orphanId) is not null,
        "integrity failure blocks unsafe startup without deleting evidence or local data");
    await verify.CloseAsync();
}

static async Task TestCrashAndConcurrencySafetyAsync(string path)
{
    var service = new DatabaseService(new EncryptionService(new MemorySecureValueStore()),
        new TestPathProvider(path));
    var attempt = new Attempt
    {
        Id = Guid.NewGuid(), ExamId = Guid.NewGuid(), AuthorizationId = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(), PackageVersion = 3,
        Status = Examifo_Desktop.Domain.Enums.AttemptStatus.InProgress,
        StartedAtUtc = DateTime.UtcNow, DeadlineUtc = DateTime.UtcNow.AddHours(1), NextSequence = 1
    };
    await service.StartAuthorizedAttemptAsync(attempt, "token");
    Answer[] answers = Enumerable.Range(0, 12).Select(i => new Answer
    {
        AttemptId = attempt.Id, QuestionId = Guid.NewGuid(), ExamQuestionId = Guid.NewGuid(),
        ResponseFormat = "text", Response = $"answer-{i}", AnsweredAtUtc = DateTime.UtcNow
    }).ToArray();
    await Task.WhenAll(answers.Select(answer => service.SaveAnswerWithOperationAsync(attempt, answer)));

    var raw = new SQLiteAsyncConnection(path);
    List<Examifo_Desktop.Infrastructure.Sync.SyncOperation> beforeSubmission =
        await raw.Table<Examifo_Desktop.Infrastructure.Sync.SyncOperation>().ToListAsync();
    Assert((await raw.Table<Answer>().CountAsync()) == answers.Length
        && beforeSubmission.Select(x => x.Sequence).Order().SequenceEqual(
            Enumerable.Range(1, answers.Length + 1).Select(x => (long)x)),
        "concurrent answer writes preserve every row and allocate unique contiguous sequences");
    await raw.CloseAsync();

    var submission = new Submission { AttemptId = attempt.Id, CreatedAtUtc = DateTime.UtcNow };
    await Task.WhenAll(Enumerable.Range(0, 6)
        .Select(_ => service.SubmitAttemptAsync(attempt, submission)));
    var restarted = new DatabaseService(new EncryptionService(new MemorySecureValueStore()),
        new TestPathProvider(path));
    var afterRestart = new SQLiteAsyncConnection(path);
    int terminals = (await afterRestart.Table<Examifo_Desktop.Infrastructure.Sync.SyncOperation>().ToListAsync())
        .Count(x => x.Type == "attempt.submitted");
    Assert(terminals == 1 && await afterRestart.Table<Submission>().CountAsync() == 1,
        "concurrent submission and restart retain exactly one terminal record and operation");
    await afterRestart.CloseAsync();
}

static async Task TestCorruptEncryptionKeyIsNotReplacedAsync()
{
    var store = new MemorySecureValueStore();
    await store.SetAsync("examifo.local_data_key.v1", "not-valid-base64");
    bool rejected = false;
    try { await new EncryptionService(store).EncryptAsync("sensitive"); }
    catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
    Assert(rejected && await store.GetAsync("examifo.local_data_key.v1") == "not-valid-base64",
        "corrupt protected key fails closed and is never silently replaced");
}

static async Task TestDurableIncrementalCatalogueAsync(string path)
{
    var service = new DatabaseService(new EncryptionService(new MemorySecureValueStore()),
        new TestPathProvider(path));
    DateTime firstRefresh = DateTime.UtcNow.AddMinutes(-2);
    DateTime secondRefresh = DateTime.UtcNow;
    Guid firstId = Guid.NewGuid();
    Guid secondId = Guid.NewGuid();
    Guid firstCandidate = Guid.NewGuid();
    Guid secondCandidate = Guid.NewGuid();
    await service.ReplaceExamCatalogueAsync(firstCandidate, [
        new AvailableExamRecord { ExamId = firstId, Title = "First", PackageVersion = 1,
            PackageHash = new string('a', 64), PackageSizeBytes = 10 },
        new AvailableExamRecord { ExamId = secondId, Title = "Second", PackageVersion = 1,
            PackageHash = new string('b', 64), PackageSizeBytes = 10 }
    ], firstRefresh, fullRefresh: true);
    await service.ReplaceExamCatalogueAsync(firstCandidate, [
        new AvailableExamRecord { ExamId = firstId, Title = "First updated", PackageVersion = 2,
            PackageHash = new string('c', 64), PackageSizeBytes = 20 }
    ], secondRefresh, fullRefresh: false);
    await service.ReplaceExamCatalogueAsync(secondCandidate, [
        new AvailableExamRecord { ExamId = secondId, Title = "Other candidate", PackageVersion = 3,
            PackageHash = new string('d', 64), PackageSizeBytes = 30 }
    ], secondRefresh, fullRefresh: true);
    List<AvailableExamRecord> cached = await service.GetCachedExamCatalogueAsync(firstCandidate);
    List<AvailableExamRecord> other = await service.GetCachedExamCatalogueAsync(secondCandidate);
    Assert(cached.Count == 2 && cached.Single(x => x.ExamId == firstId).PackageVersion == 2
        && cached.Single(x => x.ExamId == secondId).PackageVersion == 1
        && other.Count == 1 && other[0].Title == "Other candidate"
        && await service.GetExamCatalogueCheckpointAsync(firstCandidate) == secondRefresh,
        "catalogue refresh is durable and isolated between candidates");

    await service.ReplaceExamCatalogueAsync(firstCandidate, [
        new AvailableExamRecord { ExamId = firstId, Title = "Authoritative", PackageVersion = 4,
            PackageHash = new string('e', 64), PackageSizeBytes = 40 }
    ], secondRefresh.AddMinutes(1), fullRefresh: true);
    cached = await service.GetCachedExamCatalogueAsync(firstCandidate);
    other = await service.GetCachedExamCatalogueAsync(secondCandidate);
    Assert(cached.Count == 1 && cached[0].ExamId == firstId
        && cached[0].Title == "Authoritative" && other.Count == 1,
        "full catalogue replacement removes stale exams only for the current candidate");
}

static async Task TestDownloadedPackageActivationAsync(string path)
{
    var service = new DatabaseService(new EncryptionService(new MemorySecureValueStore()),
        new TestPathProvider(path));
    Guid examId = Guid.NewGuid();
    var first = new DownloadedExamRecord
    {
        ExamId = examId, PackageVersion = 1, ContentHash = new string('a', 64),
        LocalPath = @"C:\managed\v1.examifo", DownloadedAtUtc = DateTime.UtcNow,
        State = "Ready"
    };
    await service.SaveDownloadedExamAsync(first);
    DownloadedExamRecord? restored = await service.GetDownloadedExamAsync(examId);
    Assert(restored is { PackageVersion: 1, State: "Ready" }
        && restored.LocalPath == first.LocalPath,
        "downloaded package activation survives database restart boundaries");

    await service.SaveDownloadedExamAsync(new DownloadedExamRecord
    {
        ExamId = examId, PackageVersion = 2, ContentHash = new string('b', 64),
        LocalPath = @"C:\managed\v2.examifo", DownloadedAtUtc = DateTime.UtcNow,
        State = "Ready"
    });
    await service.SetDownloadedExamStateAsync(examId, "Corrupt");
    DownloadedExamRecord? replaced = await service.GetDownloadedExamAsync(examId);
    Assert(replaced is { PackageVersion: 2, State: "Corrupt" }
        && replaced.LocalPath.EndsWith("v2.examifo", StringComparison.Ordinal),
        "package activation atomically replaces metadata and supports corruption quarantine");
}

static async Task TestLogicalUniquenessConstraintsAsync(string path)
{
    var connection = new SQLiteAsyncConnection(path);
    await new DatabaseSchemaMigrator(connection).InitializeAsync();
    Guid attemptId = Guid.NewGuid();
    Guid questionId = Guid.NewGuid();
    await connection.InsertAsync(new Answer
    {
        AttemptId = attemptId, QuestionId = questionId, ExamQuestionId = Guid.NewGuid(), Response = "enc:v1:test"
    });

    bool duplicateAnswerRejected = false;
    try
    {
        await connection.InsertAsync(new Answer
        {
            AttemptId = attemptId, QuestionId = questionId, ExamQuestionId = Guid.NewGuid(), Response = "enc:v1:test2"
        });
    }
    catch (SQLiteException) { duplicateAnswerRejected = true; }

    await connection.InsertAsync(new Submission { AttemptId = attemptId });
    bool duplicateSubmissionRejected = false;
    try { await connection.InsertAsync(new Submission { AttemptId = attemptId }); }
    catch (SQLiteException) { duplicateSubmissionRejected = true; }

    Assert(duplicateAnswerRejected && duplicateSubmissionRejected,
        "database rejects duplicate logical answers and submissions");
    await connection.CloseAsync();
}

static async Task TestDurableAttemptLifecycleAsync(string path)
{
    var secureStore = new MemorySecureValueStore();
    var service = new DatabaseService(new EncryptionService(secureStore), new TestPathProvider(path));
    DateTime started = DateTime.UtcNow;
    var attempt = new Attempt
    {
        Id = Guid.NewGuid(), ExamId = Guid.NewGuid(), AuthorizationId = Guid.NewGuid(),
        CandidateId = Guid.NewGuid(), DeviceId = Guid.NewGuid(), PackageVersion = 4,
        Status = Examifo_Desktop.Domain.Enums.AttemptStatus.InProgress,
        StartedAtUtc = started, DeadlineUtc = started.AddHours(1), NextSequence = 1
    };
    await service.StartAuthorizedAttemptAsync(attempt, "authorization-token");
    long sequenceAfterStart = attempt.NextSequence;
    await service.StartAuthorizedAttemptAsync(attempt, "authorization-token");
    var startCheck = new SQLiteAsyncConnection(path);
    int startOperations = (await startCheck.Table<Examifo_Desktop.Infrastructure.Sync.SyncOperation>()
        .ToListAsync()).Count(x => x.AttemptId == attempt.Id && x.Type == "attempt.started");
    await startCheck.CloseAsync();
    Assert(startOperations == 1 && attempt.NextSequence == sequenceAfterStart,
        "repeated authorized start reuses the existing attempt and start operation");
    Assert((await service.GetLatestAttemptForExamAsync(
        attempt.ExamId, attempt.CandidateId, attempt.DeviceId))?.Id == attempt.Id,
        "latest local attempt can be located by exam for offline resume routing");
    Assert((await service.GetInProgressAttemptForExamAsync(
        attempt.ExamId, attempt.CandidateId, attempt.DeviceId))?.Id == attempt.Id,
        "in-progress attempt is preferred for resume even when other attempt history exists");

    string[] formats =
    ["selected_options", "boolean", "text", "essay", "math", "drawing", "multi_part", "table_grid", "code"];
    var answers = new List<Answer>();
    foreach (string format in formats)
    {
        var answer = new Answer
        {
            AttemptId = attempt.Id, QuestionId = Guid.NewGuid(), ExamQuestionId = Guid.NewGuid(),
            ResponseFormat = format, Response = $"{format}-private-response", AnsweredAtUtc = DateTime.UtcNow
        };
        await service.SaveAnswerWithOperationAsync(attempt, answer);
        answers.Add(answer);
    }
    Assert((await service.GetAnswersAsync(attempt.Id)).Select(x => x.ResponseFormat).Order()
        .SequenceEqual(formats.Order()), "all supported answer formats persist through the atomic path");

    Answer replaced = answers[2];
    replaced.Response = "replacement-private-response";
    replaced.AnsweredAtUtc = DateTime.UtcNow;
    await service.SaveAnswerWithOperationAsync(attempt, replaced);
    Assert((await service.GetAnswerAsync(attempt.Id, replaced.QuestionId)) is { Revision: 2,
        Response: "replacement-private-response" }, "answer replacement retains identity and increments revision");

    Answer cleared = answers[3];
    await service.ClearAnswerWithOperationAsync(attempt, cleared.QuestionId, cleared.ExamQuestionId, DateTime.UtcNow);
    Assert(await service.GetAnswerAsync(attempt.Id, cleared.QuestionId) is null,
        "answer clearing removes the local value and records an operation");

    await service.UpdateAttemptProgressAsync(attempt.Id, 5, DateTime.UtcNow);
    var restarted = new DatabaseService(new EncryptionService(secureStore), new TestPathProvider(path));
    AttemptRecoverySnapshot? recovery = await restarted.GetRecoverableAttemptAsync(
        attempt.CandidateId, attempt.DeviceId);
    Assert(recovery?.Attempt.Id == attempt.Id && recovery.Attempt.CurrentQuestionIndex == 5
        && recovery.Answers.Count == formats.Length - 1 && recovery.PendingOperationCount == formats.Length + 3,
        "restart recovery restores progress, answers, and pending operation count");

    var submission = new Submission { AttemptId = attempt.Id, CreatedAtUtc = DateTime.UtcNow };
    attempt.SubmittedAtUtc = DateTime.UtcNow;
    await service.SubmitAttemptAsync(attempt, submission);
    long sequenceAfterFirstSubmission = attempt.NextSequence;
    await service.SubmitAttemptAsync(attempt, submission);
    Assert(attempt.NextSequence == sequenceAfterFirstSubmission,
        "repeated local submission does not create another terminal operation");

    bool editRejected = false;
    try
    {
        answers[0].Response = "forbidden edit";
        await service.SaveAnswerWithOperationAsync(attempt, answers[0]);
    }
    catch (InvalidOperationException) { editRejected = true; }
    Assert(editRejected, "submitted attempts are immutable");

    List<Examifo_Desktop.Infrastructure.Sync.SyncOperation> claimed = await service.ClaimPendingOperationsAsync();
    string[] documentFormats = ["math", "drawing", "multi_part", "table_grid"];
    List<JsonElement> answerPayloads = claimed.Where(x => x.Type == "answer.upserted")
        .Select(x => JsonDocument.Parse(x.PayloadJson).RootElement.Clone()).ToList();
    Assert(documentFormats.All(format => answerPayloads.Any(payload =>
            payload.TryGetProperty("responseFormat", out JsonElement value)
            && value.GetString() == format && payload.TryGetProperty("responseDocument", out _))),
        "advanced answer formats synchronize through responseDocument payloads");
    Assert(claimed.Count == sequenceAfterFirstSubmission - 1
        && (await service.GetAttemptAsync(attempt.Id))?.Status == Examifo_Desktop.Domain.Enums.AttemptStatus.Syncing,
        "outbox claim is ordered, durable, and moves a submitted attempt to syncing");
    await service.ReturnOperationsForRetryAsync(claimed.Select(x => x.OperationId), DateTime.UtcNow.AddSeconds(-1));
    List<Examifo_Desktop.Infrastructure.Sync.SyncOperation> retried = await service.ClaimPendingOperationsAsync();
    Assert(retried.Count == claimed.Count, "retry-later operations become claimable at their scheduled time");
    await service.RecoverStaleInFlightAsync(DateTime.UtcNow.AddMinutes(1));
    List<Examifo_Desktop.Infrastructure.Sync.SyncOperation> recovered = await service.ClaimPendingOperationsAsync();
    Assert(recovered.Count == claimed.Count, "stale in-flight operations return safely to pending");

    var terminal = recovered.Single(x => x.Type == "attempt.submitted");
    await service.ApplySyncResultAsync(terminal.OperationId, "Accepted", null, 12);
    Assert((await service.GetAttemptAsync(attempt.Id))?.Status == Examifo_Desktop.Domain.Enums.AttemptStatus.Synced,
        "accepted terminal operation moves the attempt to synced");

    bool invalidTransitionRejected = false;
    try
    {
        await service.TransitionAttemptAsync(attempt.Id, Examifo_Desktop.Domain.Enums.AttemptStatus.InProgress);
    }
    catch (InvalidOperationException) { invalidTransitionRejected = true; }
    Assert(invalidTransitionRejected, "terminal attempt state cannot return to in-progress");

    var raw = new SQLiteAsyncConnection(path);
    List<Answer> rawAnswers = await raw.Table<Answer>().ToListAsync();
    Assert(rawAnswers.All(x => x.Response.StartsWith("enc:v1:")),
        "generalized answer formats remain encrypted in raw SQLite");
    int clearedOperations = (await raw.Table<Examifo_Desktop.Infrastructure.Sync.SyncOperation>().ToListAsync())
        .Count(x => x.Type == "answer.cleared");
    int submittedOperations = (await raw.Table<Examifo_Desktop.Infrastructure.Sync.SyncOperation>().ToListAsync())
        .Count(x => x.Type == "attempt.submitted");
    Assert(clearedOperations == 1 && submittedOperations == 1,
        "clear and submission each produce exactly one durable operation");
    await raw.CloseAsync();
}

static async Task TestCandidateAttemptIsolationAsync(string path)
{
    var service = new DatabaseService(
        new EncryptionService(new MemorySecureValueStore()), new TestPathProvider(path));
    Guid examId = Guid.NewGuid();
    Guid firstCandidate = Guid.NewGuid();
    Guid secondCandidate = Guid.NewGuid();
    Guid firstDevice = Guid.NewGuid();
    Guid secondDevice = Guid.NewGuid();
    DateTime started = DateTime.UtcNow;

    var first = new Attempt
    {
        Id = Guid.NewGuid(), ExamId = examId, CandidateId = firstCandidate,
        AuthorizationId = Guid.NewGuid(), DeviceId = firstDevice, PackageVersion = 1,
        Status = Examifo_Desktop.Domain.Enums.AttemptStatus.InProgress,
        StartedAtUtc = started, DeadlineUtc = started.AddHours(1), LastActivityUtc = started
    };
    var second = new Attempt
    {
        Id = Guid.NewGuid(), ExamId = examId, CandidateId = secondCandidate,
        AuthorizationId = Guid.NewGuid(), DeviceId = secondDevice, PackageVersion = 1,
        Status = Examifo_Desktop.Domain.Enums.AttemptStatus.InProgress,
        StartedAtUtc = started.AddSeconds(1), DeadlineUtc = started.AddHours(1),
        LastActivityUtc = started.AddSeconds(1)
    };
    await service.StartAuthorizedAttemptAsync(first, "first-token", "first-seed");
    await service.StartAuthorizedAttemptAsync(second, "second-token", "second-seed");

    Assert((await service.GetInProgressAttemptForExamAsync(examId, firstCandidate, firstDevice))?.Id
        == first.Id, "candidate A resumes only candidate A's attempt for the same exam");
    Assert((await service.GetInProgressAttemptForExamAsync(examId, secondCandidate, secondDevice))?.Id
        == second.Id, "candidate B resumes only candidate B's attempt for the same exam");
    Assert(await service.GetInProgressAttemptForExamAsync(examId, firstCandidate, secondDevice) is null,
        "candidate and device ownership must both match before an attempt is exposed");
}

static async Task<bool> TableExistsAsync(SQLiteAsyncConnection connection, string tableName)
{
    int count = await connection.ExecuteScalarAsync<int>(
        "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = ?", tableName);
    return count == 1;
}

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}

sealed class TestPathProvider(string databasePath) : ILocalDatabasePathProvider
{
    public string DatabasePath { get; } = databasePath;
}

sealed class MemorySecureValueStore : ISecureValueStore
{
    private readonly Dictionary<string, string> _values = [];
    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_values.TryGetValue(key, out string? value) ? value : null);
    public Task SetAsync(string key, string value)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }
    public void Remove(string key) => _values.Remove(key);
}
