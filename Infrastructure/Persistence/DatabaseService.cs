using SQLite;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Sync;
using System.Text.Json;

namespace Examifo_Desktop.Infrastructure.Persistence;

public class DatabaseService
{
    private readonly SQLiteAsyncConnection _database;

    private bool _initialized;

    public DatabaseService()
    {
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

    public async Task StartAuthorizedAttemptAsync(Attempt attempt, string authorizationToken)
    {
        await InitializeAsync();
        await _database.RunInTransactionAsync(connection =>
        {
            connection.InsertOrReplace(attempt);
            connection.Insert(new SyncOperation
            {
                AttemptId = attempt.Id,
                AuthorizationId = attempt.AuthorizationId,
                Sequence = attempt.NextSequence,
                Type = "attempt.started",
                OccurredAtUtc = attempt.StartedAtUtc,
                PackageVersion = attempt.PackageVersion,
                PayloadJson = JsonSerializer.Serialize(new { authorizationToken })
            });
            attempt.NextSequence++;
            connection.Update(attempt);
        });
    }

    public async Task SaveAnswerWithOperationAsync(Attempt attempt, Answer answer)
    {
        await InitializeAsync();
        await _database.RunInTransactionAsync(connection =>
        {
            connection.InsertOrReplace(answer);
            connection.Insert(new SyncOperation
            {
                AttemptId = attempt.Id,
                AuthorizationId = attempt.AuthorizationId,
                Sequence = attempt.NextSequence,
                Type = "answer.upserted",
                OccurredAtUtc = answer.AnsweredAtUtc,
                PackageVersion = attempt.PackageVersion,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    answerId = answer.Id,
                    examQuestionId = answer.ExamQuestionId,
                    questionId = answer.QuestionId,
                    responseFormat = "selected_options",
                    selectedOptionIds = answer.SelectedOptionId.HasValue
                        ? new[] { answer.SelectedOptionId.Value }
                        : Array.Empty<Guid>()
                })
            });
            attempt.NextSequence++;
            connection.Update(attempt);
        });
    }

    public async Task SubmitAttemptAsync(Attempt attempt, Submission submission)
    {
        await InitializeAsync();
        await _database.RunInTransactionAsync(connection =>
        {
            connection.Update(attempt);
            connection.InsertOrReplace(submission);
            connection.Insert(new SyncOperation
            {
                AttemptId = attempt.Id,
                AuthorizationId = attempt.AuthorizationId,
                Sequence = attempt.NextSequence,
                Type = "attempt.submitted",
                OccurredAtUtc = attempt.SubmittedAtUtc ?? DateTime.UtcNow,
                PackageVersion = attempt.PackageVersion,
                PayloadJson = "{}"
            });
            attempt.NextSequence++;
            connection.Update(attempt);
        });
    }

    public async Task<List<SyncOperation>> GetPendingOperationsAsync(int limit = 500)
    {
        await InitializeAsync();
        return await _database.Table<SyncOperation>()
            .Where(x => x.State == "Pending")
            .OrderBy(x => x.AttemptId)
            .ThenBy(x => x.Sequence)
            .Take(limit)
            .ToListAsync();
    }

    public async Task ApplySyncResultAsync(Guid operationId, string state, string? errorCode, long? revision)
    {
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

        return await _database.Table<Answer>()
            .Where(a =>
                a.AttemptId == attemptId &&
                a.QuestionId == questionId)
            .FirstOrDefaultAsync();
    }

    public async Task<List<Answer>> GetAnswersAsync(
        Guid attemptId)
    {
        await InitializeAsync();

        return await _database.Table<Answer>()
            .Where(a => a.AttemptId == attemptId)
            .ToListAsync();
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
