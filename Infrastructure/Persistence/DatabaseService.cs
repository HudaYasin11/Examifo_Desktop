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

    private bool _initialized;

    public DatabaseService(EncryptionService encryptionService)
    {
        _encryptionService = encryptionService;
        string databasePath = Path.Combine(
            FileSystem.AppDataDirectory,
            "examifo.db3");

        _database = new SQLiteAsyncConnection(databasePath);
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        await _database.CreateTableAsync<Attempt>();
        await _database.CreateTableAsync<Answer>();
        await _database.CreateTableAsync<Submission>();
        await _database.CreateTableAsync<SyncOperation>();
        await _database.ExecuteAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_Outbox_Attempt_Sequence ON SyncOperation (AttemptId, Sequence)");

        _initialized = true;
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

    public async Task SaveAnswerAsync(Answer answer)
    {
        await InitializeAsync();

        await _database.InsertOrReplaceAsync(answer);
    }

    public async Task StartAuthorizedAttemptAsync(Attempt attempt, string authorizationToken,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        string encryptedPayload = await _encryptionService.EncryptAsync(
            JsonSerializer.Serialize(new { authorizationToken }), cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            if (attempt.Status != Domain.Enums.AttemptStatus.InProgress)
                throw new InvalidOperationException("An attempt must be in progress before it can be started locally.");
            connection.InsertOrReplace(attempt);
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
            connection.Update(attempt);
        });
    }

    public async Task SaveAnswerWithOperationAsync(Attempt attempt, Answer answer,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        string encryptedResponse = await _encryptionService.EncryptAsync(answer.Response, cancellationToken);
        string encryptedPayload = await _encryptionService.EncryptAsync(JsonSerializer.Serialize(new
        {
            answerId = answer.Id,
            examQuestionId = answer.ExamQuestionId,
            questionId = answer.QuestionId,
            responseFormat = "selected_options",
            selectedOptionIds = answer.SelectedOptionId.HasValue
                ? new[] { answer.SelectedOptionId.Value }
                : Array.Empty<Guid>()
        }), cancellationToken);
        await _database.RunInTransactionAsync(connection =>
        {
            Attempt persistedAttempt = connection.Find<Attempt>(attempt.Id)
                ?? throw new InvalidOperationException("The local attempt no longer exists.");
            if (persistedAttempt.Status != Domain.Enums.AttemptStatus.InProgress)
                throw new InvalidOperationException("Answers cannot be changed after an attempt is submitted.");
            connection.InsertOrReplace(new Answer
            {
                Id = answer.Id,
                AttemptId = answer.AttemptId,
                QuestionId = answer.QuestionId,
                ExamQuestionId = answer.ExamQuestionId,
                SelectedOptionId = answer.SelectedOptionId,
                Response = encryptedResponse,
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
            connection.Update(persistedAttempt);
            attempt.NextSequence = persistedAttempt.NextSequence;
        });
    }

    public async Task SubmitAttemptAsync(Attempt attempt, Submission submission)
    {
        await InitializeAsync();
        await _database.RunInTransactionAsync(connection =>
        {
            Attempt persistedAttempt = connection.Find<Attempt>(attempt.Id)
                ?? throw new InvalidOperationException("The local attempt no longer exists.");
            if (persistedAttempt.Status != Domain.Enums.AttemptStatus.InProgress)
                throw new InvalidOperationException("Only an in-progress attempt can be submitted.");
            persistedAttempt.Status = Domain.Enums.AttemptStatus.SubmittedLocally;
            persistedAttempt.SubmittedAtUtc = attempt.SubmittedAtUtc ?? DateTime.UtcNow;
            connection.InsertOrReplace(submission);
            connection.Insert(new SyncOperation
            {
                AttemptId = persistedAttempt.Id,
                AuthorizationId = persistedAttempt.AuthorizationId,
                Sequence = persistedAttempt.NextSequence,
                Type = "attempt.submitted",
                OccurredAtUtc = persistedAttempt.SubmittedAtUtc.Value,
                PackageVersion = persistedAttempt.PackageVersion,
                PayloadJson = "{}"
            });
            persistedAttempt.NextSequence++;
            connection.Update(persistedAttempt);
            attempt.Status = persistedAttempt.Status;
            attempt.SubmittedAtUtc = persistedAttempt.SubmittedAtUtc;
            attempt.NextSequence = persistedAttempt.NextSequence;
        });
    }

    public async Task<List<SyncOperation>> GetPendingOperationsAsync(int limit = 500,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        List<SyncOperation> operations = await _database.Table<SyncOperation>()
            .Where(x => x.State == "Pending")
            .OrderBy(x => x.AttemptId)
            .ThenBy(x => x.Sequence)
            .Take(limit)
            .ToListAsync();
        foreach (SyncOperation operation in operations)
            operation.PayloadJson = await _encryptionService.DecryptAsync(operation.PayloadJson, cancellationToken);
        return operations;
    }

    public async Task ApplySyncResultAsync(Guid operationId, string state, string? errorCode, long? revision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync();
        SyncOperation? operation = await _database.FindAsync<SyncOperation>(operationId);
        if (operation is null) return;
        operation.State = state;
        operation.ErrorCode = errorCode;
        operation.ServerRevision = revision;
        await _database.UpdateAsync(operation);
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
