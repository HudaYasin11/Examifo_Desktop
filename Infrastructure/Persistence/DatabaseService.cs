using SQLite;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Sync;
using Examifo_Desktop.Infrastructure.Security;
using System.Text.Json;

namespace Examifo_Desktop.Infrastructure.Persistence;

public class DatabaseService
{
    private readonly SQLiteAsyncConnection _database;
    private readonly EncryptionService _encryptionService;
    private readonly DatabaseSchemaMigrator _schemaMigrator;
    private readonly SemaphoreSlim _protectionGate = new(1, 1);
    private bool _legacySensitiveValuesProtected;
    private bool _integrityVerified;

    public DatabaseService(EncryptionService encryptionService, ILocalDatabasePathProvider pathProvider)
    {
        _encryptionService = encryptionService;
        ArgumentNullException.ThrowIfNull(pathProvider);
        _database = new SQLiteAsyncConnection(pathProvider.DatabasePath);
        _schemaMigrator = new DatabaseSchemaMigrator(_database);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _schemaMigrator.InitializeAsync(cancellationToken);
            if (!_integrityVerified)
            {
                DatabaseIntegrityReport report = await VerifyIntegrityCoreAsync();
                if (!report.IsHealthy)
                    throw new DatabaseIntegrityException(report.Summary);
                _integrityVerified = true;
            }
        }
        catch (SQLiteException ex)
        {
            throw new DatabaseIntegrityException(
                "The local Examifo database could not be read safely. No local attempt data was removed.", ex);
        }
        if (_legacySensitiveValuesProtected) return;

        await _protectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_legacySensitiveValuesProtected) return;
            List<Answer> answers = await _database.Table<Answer>().ToListAsync();
            List<SyncOperation> operations = await _database.Table<SyncOperation>().ToListAsync();
            foreach (Answer answer in answers.Where(x => !IsEncrypted(x.Response)))
                answer.Response = await _encryptionService.EncryptAsync(answer.Response, cancellationToken);
            foreach (SyncOperation operation in operations.Where(x => !IsEncrypted(x.PayloadJson)))
                operation.PayloadJson = await _encryptionService.EncryptAsync(operation.PayloadJson, cancellationToken);

            await _database.RunInTransactionAsync(connection =>
            {
                foreach (Answer answer in answers.Where(x => IsEncrypted(x.Response)))
                    connection.Update(answer);
                foreach (SyncOperation operation in operations.Where(x => IsEncrypted(x.PayloadJson)))
                    connection.Update(operation);
            });
            _legacySensitiveValuesProtected = true;
        }
        finally { _protectionGate.Release(); }
    }

    private static bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith("enc:v1:", StringComparison.Ordinal);

    public async Task<DatabaseIntegrityReport> VerifyIntegrityAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _schemaMigrator.InitializeAsync(cancellationToken);
            return await VerifyIntegrityCoreAsync();
        }
        catch (SQLiteException ex)
        {
            throw new DatabaseIntegrityException(
                "The local Examifo database integrity check could not be completed. No data was removed.", ex);
        }
    }

    private async Task<DatabaseIntegrityReport> VerifyIntegrityCoreAsync()
    {
        string sqliteResult = await _database.ExecuteScalarAsync<string>("PRAGMA quick_check");
        int orphanAnswers = await _database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Answer a LEFT JOIN Attempt t ON t.Id = a.AttemptId WHERE t.Id IS NULL");
        int orphanOperations = await _database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM SyncOperation o LEFT JOIN Attempt t ON t.Id = o.AttemptId WHERE t.Id IS NULL");
        int invalidSequences = await _database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM SyncOperation WHERE Sequence < 1");
        bool healthy = string.Equals(sqliteResult, "ok", StringComparison.OrdinalIgnoreCase)
            && orphanAnswers == 0 && orphanOperations == 0 && invalidSequences == 0;
        string summary = healthy ? "ok"
            : $"SQLite={sqliteResult}; orphanAnswers={orphanAnswers}; " +
              $"orphanOperations={orphanOperations}; invalidSequences={invalidSequences}.";
        return new DatabaseIntegrityReport(healthy, sqliteResult, orphanAnswers,
            orphanOperations, invalidSequences, summary);
    }

    public async Task SaveLocalUserAsync(Guid userId, string name, string? email,
        DateTime lastOnlineLoginUtc, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A valid local user is required.");
        await InitializeAsync(cancellationToken);
        await _database.InsertOrReplaceAsync(new LocalUserRecord
        {
            UserId = userId,
            EncryptedName = await _encryptionService.EncryptAsync(name, cancellationToken),
            EncryptedEmail = string.IsNullOrWhiteSpace(email) ? null
                : await _encryptionService.EncryptAsync(email, cancellationToken),
            LastOnlineLoginUtc = DateTime.SpecifyKind(lastOnlineLoginUtc, DateTimeKind.Utc)
        });
    }

    public async Task SaveLocalDeviceAsync(LocalDeviceRecord device,
        CancellationToken cancellationToken = default)
    {
        if (device.DeviceId == Guid.Empty || device.InstallationId == Guid.Empty
            || string.IsNullOrWhiteSpace(device.EncryptedName))
            throw new ArgumentException("A valid local device is required.", nameof(device));
        await InitializeAsync(cancellationToken);
        device.EncryptedName = await _encryptionService.EncryptAsync(device.EncryptedName, cancellationToken);
        await _database.InsertOrReplaceAsync(device);
    }

    public async Task SaveDownloadedExamAsync(DownloadedExamRecord exam,
        CancellationToken cancellationToken = default)
    {
        if (exam.ExamId == Guid.Empty || exam.PackageVersion <= 0
            || string.IsNullOrWhiteSpace(exam.ContentHash) || string.IsNullOrWhiteSpace(exam.LocalPath))
            throw new ArgumentException("A valid downloaded exam is required.", nameof(exam));
        await InitializeAsync(cancellationToken);
        await _database.InsertOrReplaceAsync(exam);
    }

    public async Task<DownloadedExamRecord?> GetDownloadedExamAsync(Guid examId,
        CancellationToken cancellationToken = default)
    {
        if (examId == Guid.Empty) return null;
        await InitializeAsync(cancellationToken);
        return await _database.FindAsync<DownloadedExamRecord>(examId);
    }

    public async Task SetDownloadedExamStateAsync(Guid examId, string state,
        CancellationToken cancellationToken = default)
    {
        if (examId == Guid.Empty || string.IsNullOrWhiteSpace(state)) return;
        await InitializeAsync(cancellationToken);
        await _database.ExecuteAsync(
            "UPDATE DownloadedExamRecord SET State = ? WHERE ExamId = ?", state, examId);
    }

    public async Task ReplaceExamCatalogueAsync(Guid candidateId, IEnumerable<AvailableExamRecord> exams,
        DateTime serverRefreshUtc, bool fullRefresh,
        CancellationToken cancellationToken = default)
    {
        AvailableExamRecord[] records = exams.ToArray();
        if (candidateId == Guid.Empty || serverRefreshUtc == default
            || records.Any(x => x.ExamId == Guid.Empty))
            throw new ArgumentException("A valid exam catalogue is required.", nameof(exams));
        await InitializeAsync(cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            Dictionary<Guid, AvailableExamRecord> existing = connection.Table<AvailableExamRecord>()
                .Where(x => x.CandidateId == candidateId).ToDictionary(x => x.ExamId);
            if (fullRefresh)
                connection.Execute("DELETE FROM AvailableExamRecord WHERE CandidateId = ?", candidateId);
            foreach (AvailableExamRecord record in records)
            {
                if (record.MaxAttempts <= 0 && existing.TryGetValue(record.ExamId, out AvailableExamRecord? prior))
                {
                    record.MaxAttempts = prior.MaxAttempts;
                    record.ProctoringEnabled = prior.ProctoringEnabled;
                }
                record.CandidateId = candidateId;
                record.CacheKey = $"{candidateId:N}:{record.ExamId:N}";
                record.RefreshedAtUtc = serverRefreshUtc;
                connection.InsertOrReplace(record);
            }
            connection.InsertOrReplace(new ExamCatalogueCheckpointRecord
            {
                CandidateId = candidateId,
                LastServerRefreshUtc = serverRefreshUtc
            });
        });
    }

    public async Task UpdateCachedExamMetadataAsync(Guid candidateId, Guid examId, int maxAttempts,
        bool proctoringEnabled, CancellationToken cancellationToken = default)
    {
        if (candidateId == Guid.Empty || examId == Guid.Empty || maxAttempts <= 0)
            throw new ArgumentException("Valid authoritative exam metadata is required.");
        await InitializeAsync(cancellationToken);
        await _database.ExecuteAsync("UPDATE AvailableExamRecord SET MaxAttempts = ?, ProctoringEnabled = ? " +
            "WHERE CandidateId = ? AND ExamId = ?", maxAttempts, proctoringEnabled, candidateId, examId);
    }

    public async Task<List<AvailableExamRecord>> GetCachedExamCatalogueAsync(Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        if (candidateId == Guid.Empty) return [];
        await InitializeAsync(cancellationToken);
        return await _database.Table<AvailableExamRecord>()
            .Where(x => x.CandidateId == candidateId).OrderBy(x => x.Title).ToListAsync();
    }

    public async Task<DateTime?> GetExamCatalogueCheckpointAsync(Guid candidateId,
        CancellationToken cancellationToken = default)
    {
        if (candidateId == Guid.Empty) return null;
        await InitializeAsync(cancellationToken);
        return (await _database.FindAsync<ExamCatalogueCheckpointRecord>(candidateId))?.LastServerRefreshUtc;
    }

    public async Task SaveAttemptAuthorizationAsync(AttemptAuthorizationRecord authorization,
        string shuffleSeed, string authorizationToken, CancellationToken cancellationToken = default)
    {
        if (authorization.AuthorizationId == Guid.Empty || authorization.AttemptId == Guid.Empty
            || authorization.ExamId == Guid.Empty || authorization.CandidateId == Guid.Empty
            || authorization.DeviceId == Guid.Empty || authorization.PackageVersion <= 0
            || string.IsNullOrWhiteSpace(shuffleSeed) || string.IsNullOrWhiteSpace(authorizationToken))
            throw new ArgumentException("A valid attempt authorization is required.", nameof(authorization));
        await InitializeAsync(cancellationToken);
        authorization.EncryptedShuffleSeed = await _encryptionService.EncryptAsync(shuffleSeed, cancellationToken);
        authorization.EncryptedAuthorizationToken = await _encryptionService.EncryptAsync(
            authorizationToken, cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            connection.Execute("DELETE FROM AttemptAuthorizationRecord " +
                "WHERE AuthorizationId = ? OR AttemptId = ? OR (ExamId = ? AND CandidateId = ?)",
                authorization.AuthorizationId, authorization.AttemptId, authorization.ExamId,
                authorization.CandidateId);
            connection.Insert(authorization);
        });
    }

    public async Task RemoveAttemptAuthorizationAsync(Guid authorizationId,
        CancellationToken cancellationToken = default)
    {
        if (authorizationId == Guid.Empty) return;
        await InitializeAsync(cancellationToken);
        await _database.ExecuteAsync(
            "DELETE FROM AttemptAuthorizationRecord WHERE AuthorizationId = ?", authorizationId);
    }

    public async Task AdvanceSyncCheckpointAsync(Guid clientId, long serverRevision,
        DateTime successfulSyncUtc, string? pullCursor = null,
        CancellationToken cancellationToken = default)
    {
        if (clientId == Guid.Empty || serverRevision < 0)
            throw new ArgumentException("A valid synchronization checkpoint is required.");
        await InitializeAsync(cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            SyncCheckpointRecord? checkpoint = connection.Find<SyncCheckpointRecord>(clientId);
            if (checkpoint is null)
            {
                checkpoint = new SyncCheckpointRecord { ClientId = clientId };
                connection.Insert(checkpoint);
            }
            checkpoint.LastServerRevision = Math.Max(checkpoint.LastServerRevision, serverRevision);
            checkpoint.LastSuccessfulSyncUtc = successfulSyncUtc;
            if (pullCursor is not null) checkpoint.PullCursor = pullCursor;
            connection.Update(checkpoint);
        });
    }

    public async Task<SyncCheckpointRecord?> GetSyncCheckpointAsync(Guid clientId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await _database.FindAsync<SyncCheckpointRecord>(clientId);
    }

    public async Task RecordProctoringEventWithOperationAsync(Guid attemptId, string eventType,
        DateTime occurredAtUtc, string metadataJson,
        CancellationToken cancellationToken = default)
    {
        if (attemptId == Guid.Empty || string.IsNullOrWhiteSpace(eventType) || occurredAtUtc == default)
            throw new ArgumentException("A valid proctoring event is required.");
        await InitializeAsync(cancellationToken);
        Guid eventId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        string encryptedMetadata = await _encryptionService.EncryptAsync(
            metadataJson, cancellationToken);
        string encryptedPayload = await _encryptionService.EncryptAsync(JsonSerializer.Serialize(new
        {
            eventId,
            eventType,
            metadataJson
        }), cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            Attempt attempt = connection.Find<Attempt>(attemptId)
                ?? throw new InvalidOperationException("The local attempt no longer exists.");
            if (attempt.Status != Domain.Enums.AttemptStatus.InProgress)
                throw new InvalidOperationException("Proctoring events can only be recorded for an active attempt.");
            long sequence = attempt.NextSequence;
            connection.Insert(new ProctoringEventRecord
            {
                EventId = eventId,
                AttemptId = attemptId,
                EventType = eventType,
                OccurredAtUtc = occurredAtUtc,
                EncryptedMetadataJson = encryptedMetadata,
                Sequence = sequence,
                OperationId = operationId
            });
            connection.Insert(new SyncOperation
            {
                OperationId = operationId,
                AttemptId = attemptId,
                AuthorizationId = attempt.AuthorizationId,
                Sequence = sequence,
                Type = "proctoring.event-recorded",
                OccurredAtUtc = occurredAtUtc,
                PackageVersion = attempt.PackageVersion,
                PayloadJson = encryptedPayload
            });
            attempt.NextSequence++;
            attempt.LastActivityUtc = occurredAtUtc;
            connection.Update(attempt);
        });
    }

    public async Task SaveAttemptAsync(Attempt attempt)
    {
        await InitializeAsync();

        await _database.InsertOrReplaceAsync(attempt);
    }

    public async Task UpdateAttemptAsync(Attempt attempt)
    {
        await InitializeAsync();

        await _database.UpdateAsync(attempt);
    }

    public async Task UpdateAttemptProgressAsync(Guid attemptId, int currentQuestionIndex,
        DateTime occurredAtUtc, CancellationToken cancellationToken = default)
    {
        if (attemptId == Guid.Empty || currentQuestionIndex < 0)
            throw new ArgumentException("Valid attempt progress is required.");
        await InitializeAsync(cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            Attempt attempt = connection.Find<Attempt>(attemptId)
                ?? throw new InvalidOperationException("The local attempt no longer exists.");
            if (attempt.Status != Domain.Enums.AttemptStatus.InProgress)
                throw new InvalidOperationException("Only an in-progress attempt can update navigation state.");
            attempt.CurrentQuestionIndex = currentQuestionIndex;
            attempt.LastActivityUtc = occurredAtUtc;
            connection.Update(attempt);
        });
    }

    public async Task TransitionAttemptAsync(Guid attemptId, Domain.Enums.AttemptStatus nextState,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            Attempt attempt = connection.Find<Attempt>(attemptId)
                ?? throw new InvalidOperationException("The local attempt no longer exists.");
            if (!IsAllowedAttemptTransition(attempt.Status, nextState))
                throw new InvalidOperationException($"Invalid attempt transition: {attempt.Status} -> {nextState}.");
            attempt.Status = nextState;
            attempt.LastActivityUtc = DateTime.UtcNow;
            connection.Update(attempt);
        });
    }

    private static bool IsAllowedAttemptTransition(Domain.Enums.AttemptStatus current,
        Domain.Enums.AttemptStatus next) => (current, next) switch
    {
        (Domain.Enums.AttemptStatus.Authorized, Domain.Enums.AttemptStatus.InProgress) => true,
        (Domain.Enums.AttemptStatus.InProgress, Domain.Enums.AttemptStatus.SubmittedLocally) => true,
        (Domain.Enums.AttemptStatus.SubmittedLocally, Domain.Enums.AttemptStatus.Syncing) => true,
        (Domain.Enums.AttemptStatus.Syncing, Domain.Enums.AttemptStatus.SubmittedLocally) => true,
        (Domain.Enums.AttemptStatus.Syncing, Domain.Enums.AttemptStatus.Synced) => true,
        (Domain.Enums.AttemptStatus.Syncing, Domain.Enums.AttemptStatus.Rejected) => true,
        (_, Domain.Enums.AttemptStatus.NeedsReview) when current != Domain.Enums.AttemptStatus.Synced => true,
        _ => current == next
    };

    public async Task StartAuthorizedAttemptAsync(Attempt attempt, string authorizationToken,
        string? shuffleSeed = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        string encryptedPayload = await _encryptionService.EncryptAsync(
            JsonSerializer.Serialize(new { authorizationToken }), cancellationToken);
        string encryptedShuffleSeed = await _encryptionService.EncryptAsync(
            string.IsNullOrWhiteSpace(shuffleSeed) ? attempt.Id.ToString("N") : shuffleSeed,
            cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            if (attempt.Status != Domain.Enums.AttemptStatus.InProgress)
                throw new InvalidOperationException("An attempt must be in progress before it can be started locally.");
            Attempt? persisted = connection.Find<Attempt>(attempt.Id);
            if (persisted is not null)
            {
                bool sameIdentity = persisted.ExamId == attempt.ExamId
                    && persisted.CandidateId == attempt.CandidateId
                    && persisted.AuthorizationId == attempt.AuthorizationId
                    && persisted.DeviceId == attempt.DeviceId
                    && persisted.PackageVersion == attempt.PackageVersion;
                bool alreadyStarted = connection.Table<SyncOperation>().Any(x =>
                    x.AttemptId == attempt.Id && x.Type == "attempt.started");
                if (!sameIdentity || !alreadyStarted)
                    throw new InvalidOperationException(
                        "The existing local attempt cannot be safely reused for this authorization.");

                attempt.Status = persisted.Status;
                attempt.StartedAtUtc = persisted.StartedAtUtc;
                attempt.DeadlineUtc = persisted.DeadlineUtc;
                attempt.NextSequence = persisted.NextSequence;
                attempt.CurrentQuestionIndex = persisted.CurrentQuestionIndex;
                attempt.LastActivityUtc = persisted.LastActivityUtc;
                return;
            }

            attempt.EncryptedShuffleSeed = encryptedShuffleSeed;
            connection.Insert(attempt);
            connection.Insert(new SyncOperation
            {
                AttemptId = attempt.Id,
                AuthorizationId = attempt.AuthorizationId,
                Sequence = attempt.NextSequence,
                Type = "attempt.started",
                OccurredAtUtc = attempt.StartedAtUtc,
                PackageVersion = attempt.PackageVersion,
                PayloadJson = encryptedPayload
            });
            attempt.NextSequence++;
            attempt.LastActivityUtc = attempt.StartedAtUtc;
            connection.Update(attempt);
        });
    }

    public async Task SaveAnswerWithOperationAsync(Attempt attempt, Answer answer,
        CancellationToken cancellationToken = default)
    {
        ValidateAnswer(answer);
        await InitializeAsync();
        Guid[] selectedOptionIds = GetSelectedOptionIds(answer);
        string encryptedResponse = await _encryptionService.EncryptAsync(answer.Response, cancellationToken);
        string encryptedPayload = await _encryptionService.EncryptAsync(
            JsonSerializer.Serialize(BuildAnswerPayload(answer, selectedOptionIds)), cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            Attempt persistedAttempt = connection.Find<Attempt>(attempt.Id)
                ?? throw new InvalidOperationException("The local attempt no longer exists.");
            if (persistedAttempt.Status != Domain.Enums.AttemptStatus.InProgress)
                throw new InvalidOperationException("Answers cannot be changed after an attempt is submitted.");
            Answer? previous = connection.Table<Answer>().FirstOrDefault(x =>
                x.AttemptId == answer.AttemptId && x.QuestionId == answer.QuestionId);
            long revision = (previous?.Revision ?? 0) + 1;
            connection.InsertOrReplace(new Answer
            {
                Id = previous?.Id ?? answer.Id,
                AttemptId = answer.AttemptId,
                QuestionId = answer.QuestionId,
                ExamQuestionId = answer.ExamQuestionId,
                SelectedOptionId = answer.SelectedOptionId,
                ResponseFormat = answer.ResponseFormat,
                Response = encryptedResponse,
                Revision = revision,
                AnsweredAtUtc = answer.AnsweredAtUtc
            });
            connection.Insert(new SyncOperation
            {
                AttemptId = persistedAttempt.Id,
                AuthorizationId = persistedAttempt.AuthorizationId,
                Sequence = persistedAttempt.NextSequence,
                Type = "answer.upserted",
                OccurredAtUtc = answer.AnsweredAtUtc,
                PackageVersion = persistedAttempt.PackageVersion,
                PayloadJson = encryptedPayload
            });
            persistedAttempt.NextSequence++;
            persistedAttempt.LastActivityUtc = answer.AnsweredAtUtc;
            connection.Update(persistedAttempt);
            attempt.NextSequence = persistedAttempt.NextSequence;
            attempt.LastActivityUtc = persistedAttempt.LastActivityUtc;
            answer.Id = previous?.Id ?? answer.Id;
            answer.Revision = revision;
        });
    }

    private static Guid[] GetSelectedOptionIds(Answer answer)
    {
        if (answer.SelectedOptionId.HasValue) return [answer.SelectedOptionId.Value];
        if (!string.Equals(answer.ResponseFormat, "selected_options", StringComparison.Ordinal)) return [];
        try { return JsonSerializer.Deserialize<Guid[]>(answer.Response) ?? []; }
        catch (JsonException) { return []; }
    }

    private static Dictionary<string, object?> BuildAnswerPayload(Answer answer, Guid[] selectedOptionIds)
    {
        var payload = new Dictionary<string, object?>
        {
            ["answerId"] = answer.Id,
            ["examQuestionId"] = answer.ExamQuestionId,
            ["questionId"] = answer.QuestionId,
            ["responseFormat"] = answer.ResponseFormat
        };
        if (answer.ResponseFormat == "selected_options") payload["selectedOptionIds"] = selectedOptionIds;
        else if (answer.ResponseFormat is "text" or "essay") payload["responseText"] = answer.Response;
        else if (answer.ResponseFormat == "code")
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(answer.Response);
                payload["codeLanguage"] = document.RootElement.TryGetProperty("language", out JsonElement language)
                    ? language.GetString() : null;
                payload["codeSubmission"] = document.RootElement.TryGetProperty("submission", out JsonElement submission)
                    ? submission.GetString() : string.Empty;
            }
            catch (JsonException)
            {
                payload["codeLanguage"] = null;
                payload["codeSubmission"] = answer.Response;
            }
        }
        else if (answer.ResponseFormat is "math" or "drawing" or "multi_part" or "table_grid")
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(answer.Response);
                payload["responseDocument"] = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                payload["responseDocument"] = new Dictionary<string, object?>
                {
                    ["value"] = answer.Response
                };
            }
        }
        return payload;
    }

    public async Task ClearAnswerWithOperationAsync(Attempt attempt, Guid questionId,
        Guid examQuestionId, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
    {
        if (questionId == Guid.Empty || examQuestionId == Guid.Empty)
            throw new ArgumentException("Valid question identifiers are required.");
        await InitializeAsync(cancellationToken);
        string encryptedPayload = await _encryptionService.EncryptAsync(JsonSerializer.Serialize(new
        {
            questionId,
            examQuestionId
        }), cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            Attempt persistedAttempt = connection.Find<Attempt>(attempt.Id)
                ?? throw new InvalidOperationException("The local attempt no longer exists.");
            if (persistedAttempt.Status != Domain.Enums.AttemptStatus.InProgress)
                throw new InvalidOperationException("Answers cannot be cleared after an attempt is submitted.");
            connection.Execute("DELETE FROM Answer WHERE AttemptId = ? AND QuestionId = ?",
                attempt.Id, questionId);
            connection.Insert(new SyncOperation
            {
                AttemptId = persistedAttempt.Id,
                AuthorizationId = persistedAttempt.AuthorizationId,
                Sequence = persistedAttempt.NextSequence,
                Type = "answer.cleared",
                OccurredAtUtc = occurredAtUtc,
                PackageVersion = persistedAttempt.PackageVersion,
                PayloadJson = encryptedPayload
            });
            persistedAttempt.NextSequence++;
            persistedAttempt.LastActivityUtc = occurredAtUtc;
            connection.Update(persistedAttempt);
            attempt.NextSequence = persistedAttempt.NextSequence;
            attempt.LastActivityUtc = persistedAttempt.LastActivityUtc;
        });
    }

    private static void ValidateAnswer(Answer answer)
    {
        string[] supportedFormats =
        [
            "selected_options", "boolean", "text", "essay", "math", "drawing",
            "multi_part", "table_grid", "code"
        ];
        if (answer.Id == Guid.Empty || answer.AttemptId == Guid.Empty || answer.QuestionId == Guid.Empty
            || answer.ExamQuestionId == Guid.Empty || !supportedFormats.Contains(answer.ResponseFormat)
            || answer.AnsweredAtUtc == default)
            throw new ArgumentException("A valid supported answer is required.", nameof(answer));
    }

    public async Task SubmitAttemptAsync(Attempt attempt, Submission submission)
    {
        await InitializeAsync();
        string encryptedPayload = await _encryptionService.EncryptAsync("{}");
        await _database.RunInTransactionAsync(connection =>
        {
            Attempt persistedAttempt = connection.Find<Attempt>(attempt.Id)
                ?? throw new InvalidOperationException("The local attempt no longer exists.");
            if (persistedAttempt.Status != Domain.Enums.AttemptStatus.InProgress)
            {
                Submission? existing = connection.Table<Submission>()
                    .FirstOrDefault(x => x.AttemptId == attempt.Id);
                if (existing is not null && persistedAttempt.Status is
                    Domain.Enums.AttemptStatus.SubmittedLocally or Domain.Enums.AttemptStatus.Syncing
                    or Domain.Enums.AttemptStatus.Synced)
                {
                    attempt.Status = persistedAttempt.Status;
                    attempt.SubmittedAtUtc = persistedAttempt.SubmittedAtUtc;
                    attempt.NextSequence = persistedAttempt.NextSequence;
                    return;
                }
                throw new InvalidOperationException("Only an in-progress attempt can be submitted.");
            }
            persistedAttempt.Status = Domain.Enums.AttemptStatus.SubmittedLocally;
            persistedAttempt.SubmittedAtUtc = attempt.SubmittedAtUtc ?? DateTime.UtcNow;
            persistedAttempt.LastActivityUtc = persistedAttempt.SubmittedAtUtc.Value;
            connection.InsertOrReplace(submission);
            connection.Insert(new SyncOperation
            {
                AttemptId = persistedAttempt.Id,
                AuthorizationId = persistedAttempt.AuthorizationId,
                Sequence = persistedAttempt.NextSequence,
                Type = "attempt.submitted",
                OccurredAtUtc = persistedAttempt.SubmittedAtUtc.Value,
                PackageVersion = persistedAttempt.PackageVersion,
                PayloadJson = encryptedPayload
            });
            persistedAttempt.NextSequence++;
            connection.Update(persistedAttempt);
            attempt.Status = persistedAttempt.Status;
            attempt.SubmittedAtUtc = persistedAttempt.SubmittedAtUtc;
            attempt.NextSequence = persistedAttempt.NextSequence;
        });
    }

    public async Task<List<SyncOperation>> ClaimPendingOperationsAsync(int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 500) throw new ArgumentOutOfRangeException(nameof(limit));
        await InitializeAsync();
        var operations = new List<SyncOperation>();
        DateTime now = DateTime.UtcNow;
        await _database.RunInTransactionAsync(connection =>
        {
            operations = connection.Table<SyncOperation>()
                .Where(x => x.State == OutboxStates.Pending || x.State == OutboxStates.RetryLater)
                .ToList()
                .Where(x => x.NextAttemptAtUtc is null || x.NextAttemptAtUtc <= now)
                .OrderBy(x => x.AttemptId).ThenBy(x => x.Sequence).Take(limit).ToList();
            foreach (SyncOperation operation in operations)
            {
                operation.State = OutboxStates.InFlight;
                operation.InFlightAtUtc = now;
                operation.LastAttemptAtUtc = now;
                connection.Update(operation);
                if (operation.Type == "attempt.submitted")
                {
                    Attempt? attempt = connection.Find<Attempt>(operation.AttemptId);
                    if (attempt?.Status == Domain.Enums.AttemptStatus.SubmittedLocally)
                    {
                        attempt.Status = Domain.Enums.AttemptStatus.Syncing;
                        connection.Update(attempt);
                    }
                }
            }
        });
        foreach (SyncOperation operation in operations)
            operation.PayloadJson = await _encryptionService.DecryptAsync(operation.PayloadJson, cancellationToken);
        return operations;
    }

    public async Task RecoverStaleInFlightAsync(DateTime staleBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            foreach (SyncOperation operation in connection.Table<SyncOperation>()
                .Where(x => x.State == OutboxStates.InFlight).ToList()
                .Where(x => x.InFlightAtUtc is null || x.InFlightAtUtc <= staleBeforeUtc))
            {
                operation.State = OutboxStates.Pending;
                operation.InFlightAtUtc = null;
                connection.Update(operation);
            }
        });
    }

    public async Task ReturnOperationsForRetryAsync(IEnumerable<Guid> operationIds, DateTime nextAttemptAtUtc,
        CancellationToken cancellationToken = default)
    {
        Guid[] ids = operationIds.Distinct().ToArray();
        await InitializeAsync(cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            foreach (Guid id in ids)
            {
                SyncOperation? operation = connection.Find<SyncOperation>(id);
                if (operation is null || operation.State != OutboxStates.InFlight) continue;
                operation.State = OutboxStates.RetryLater;
                operation.RetryCount++;
                operation.NextAttemptAtUtc = nextAttemptAtUtc;
                operation.InFlightAtUtc = null;
                connection.Update(operation);
            }
        });
    }

    public async Task ApplySyncResultAsync(Guid operationId, string state, string? errorCode, long? revision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync();
        string normalizedState = state.ToLowerInvariant() switch
        {
            "accepted" => OutboxStates.Accepted,
            "duplicate" => OutboxStates.Duplicate,
            "rejected" => OutboxStates.Rejected,
            "retrylater" or "retry_later" => OutboxStates.RetryLater,
            _ => throw new ArgumentException("Unknown outbox result state.", nameof(state))
        };
        await _database.RunInTransactionAsync(connection =>
        {
            SyncOperation? operation = connection.Find<SyncOperation>(operationId);
            if (operation is null) return;
            operation.State = normalizedState;
            operation.ErrorCode = errorCode;
            operation.ServerRevision = revision;
            operation.InFlightAtUtc = null;
            if (normalizedState == OutboxStates.RetryLater)
            {
                operation.RetryCount++;
                operation.NextAttemptAtUtc = DateTime.UtcNow.AddSeconds(5);
            }
            connection.Update(operation);
            if (operation.Type != "attempt.submitted") return;
            Attempt? attempt = connection.Find<Attempt>(operation.AttemptId);
            if (attempt is null) return;
            attempt.Status = normalizedState switch
            {
                OutboxStates.Accepted or OutboxStates.Duplicate => Domain.Enums.AttemptStatus.Synced,
                OutboxStates.Rejected => Domain.Enums.AttemptStatus.Rejected,
                _ => Domain.Enums.AttemptStatus.SubmittedLocally
            };
            connection.Update(attempt);
        });
    }

    public async Task<AttemptRecoverySnapshot?> GetRecoverableAttemptAsync(Guid candidateId, Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        List<Attempt> attempts = await _database.Table<Attempt>().ToListAsync();
        Attempt? attempt = attempts
            .Where(x => x.Status is Domain.Enums.AttemptStatus.InProgress
                or Domain.Enums.AttemptStatus.SubmittedLocally or Domain.Enums.AttemptStatus.Syncing
                or Domain.Enums.AttemptStatus.Rejected or Domain.Enums.AttemptStatus.NeedsReview)
            .Where(x => x.CandidateId == candidateId && x.DeviceId == deviceId)
            .OrderByDescending(x => x.LastActivityUtc).FirstOrDefault();
        if (attempt is null) return null;
        List<Answer> answers = await GetAnswersAsync(attempt.Id);
        int pendingOperations = (await _database.Table<SyncOperation>().ToListAsync()).Count(x =>
            x.AttemptId == attempt.Id && x.State is OutboxStates.Pending or OutboxStates.InFlight
                or OutboxStates.RetryLater);
        return new AttemptRecoverySnapshot(attempt, answers, pendingOperations);
    }

    public async Task<Answer?> GetAnswerAsync(
        Guid attemptId,
        Guid questionId)
    {
        await InitializeAsync();

        Answer? answer = await _database.Table<Answer>()
            .Where(a =>
                a.AttemptId == attemptId &&
                a.QuestionId == questionId)
            .FirstOrDefaultAsync();
        if (answer is not null)
            answer.Response = await _encryptionService.DecryptAsync(answer.Response);
        return answer;
    }

    public async Task<List<Answer>> GetAnswersAsync(
        Guid attemptId)
    {
        await InitializeAsync();

        List<Answer> answers = await _database.Table<Answer>()
            .Where(a => a.AttemptId == attemptId)
            .ToListAsync();
        foreach (Answer answer in answers)
            answer.Response = await _encryptionService.DecryptAsync(answer.Response);
        return answers;
    }

    public async Task<Attempt?> GetAttemptAsync(
        Guid attemptId)
    {
        await InitializeAsync();

        return await _database.Table<Attempt>()
            .Where(a => a.Id == attemptId)
            .FirstOrDefaultAsync();
    }

    public async Task<Attempt?> GetLatestAttemptForExamAsync(Guid examId, Guid candidateId, Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return (await _database.Table<Attempt>().Where(x => x.ExamId == examId
                && x.CandidateId == candidateId && x.DeviceId == deviceId).ToListAsync())
            .OrderByDescending(x => x.LastActivityUtc).ThenByDescending(x => x.StartedAtUtc)
            .FirstOrDefault();
    }

    public async Task<Attempt?> GetInProgressAttemptForExamAsync(Guid examId, Guid candidateId, Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        List<Attempt> candidates = await _database.Table<Attempt>().Where(x =>
                x.ExamId == examId && x.Status == Domain.Enums.AttemptStatus.InProgress
                && x.DeviceId == deviceId
                && (x.CandidateId == candidateId || x.CandidateId == Guid.Empty))
            .ToListAsync();
        Attempt? attempt = candidates
            .OrderByDescending(x => x.CandidateId == candidateId)
            .ThenByDescending(x => x.LastActivityUtc).ThenByDescending(x => x.StartedAtUtc)
            .FirstOrDefault();
        if (attempt is not null && attempt.CandidateId == Guid.Empty)
        {
            // Safe legacy adoption: the authenticated backend device must match. Rows owned
            // by a different device are never surfaced or reassigned.
            attempt.CandidateId = candidateId;
            await _database.UpdateAsync(attempt);
        }
        if (attempt is not null)
            attempt.ShuffleSeed = string.IsNullOrWhiteSpace(attempt.EncryptedShuffleSeed)
                ? attempt.Id.ToString("N")
                : await _encryptionService.DecryptAsync(attempt.EncryptedShuffleSeed, cancellationToken);
        return attempt;
    }

    public async Task<List<Attempt>> GetAttemptsAsync()
    {
        await InitializeAsync();

        return await _database
            .Table<Attempt>()
            .ToListAsync();
    }

    public async Task SaveSubmissionAsync(
        Submission submission)
    {
        await InitializeAsync();

        await _database.InsertOrReplaceAsync(
            submission);
    }

    public async Task<List<Submission>> GetSubmissionsAsync()
    {
        await InitializeAsync();

        return await _database
            .Table<Submission>()
            .ToListAsync();
    }
}

public sealed record AttemptRecoverySnapshot(
    Attempt Attempt,
    IReadOnlyList<Answer> Answers,
    int PendingOperationCount);

public sealed record DatabaseIntegrityReport(
    bool IsHealthy,
    string SqliteResult,
    int OrphanAnswerCount,
    int OrphanOperationCount,
    int InvalidSequenceCount,
    string Summary);

public sealed class DatabaseIntegrityException : Exception
{
    public DatabaseIntegrityException(string message) : base(message) { }
    public DatabaseIntegrityException(string message, Exception innerException) : base(message, innerException) { }
}
