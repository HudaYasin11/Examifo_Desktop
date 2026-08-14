using SQLite;
using Examifo_Desktop.Domain.Models;

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