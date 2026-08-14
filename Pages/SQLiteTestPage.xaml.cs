using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Pages;

public partial class SQLiteTestPage : ContentPage
{
    private readonly DatabaseService _databaseService;

    public SQLiteTestPage(DatabaseService databaseService)
    {
        InitializeComponent();

        _databaseService = databaseService;
    }

    private async void LoadDatabaseButton_Clicked(
        object sender,
        EventArgs e)
    {
        var attempts =
            await _databaseService.GetAttemptsAsync();

        var submissions =
            await _databaseService.GetSubmissionsAsync();

        AttemptsLabel.Text = string.Empty;

        if (attempts.Count == 0)
        {
            AttemptsLabel.Text = "No attempts found.";
        }
        else
        {
            foreach (var attempt in attempts)
            {
                AttemptsLabel.Text +=
                    $"ID: {attempt.Id}\n" +
                    $"Exam ID: {attempt.ExamId}\n" +
                    $"Status: {attempt.Status}\n" +
                    $"Started: {attempt.StartedAtUtc}\n" +
                    $"Submitted: {attempt.SubmittedAtUtc}\n\n";
            }
        }

        AnswersLabel.Text = string.Empty;

        foreach (var attempt in attempts)
        {
            var answers =
                await _databaseService.GetAnswersAsync(
                    attempt.Id);

            if (answers.Count == 0)
            {
                AnswersLabel.Text +=
                    $"Attempt {attempt.Id}: No answers found.\n\n";

                continue;
            }

            foreach (var answer in answers)
            {
                AnswersLabel.Text +=
                    $"Attempt ID: {answer.AttemptId}\n" +
                    $"Question ID: {answer.QuestionId}\n" +
                    $"Response: {answer.Response}\n" +
                    $"Answered: {answer.AnsweredAtUtc}\n\n";
            }
        }

        if (string.IsNullOrWhiteSpace(AnswersLabel.Text))
        {
            AnswersLabel.Text = "No answers found.";
        }

        SubmissionsLabel.Text = string.Empty;

        if (submissions.Count == 0)
        {
            SubmissionsLabel.Text =
                "No submissions found.";
        }
        else
        {
            foreach (var submission in submissions)
            {
                SubmissionsLabel.Text +=
                    $"ID: {submission.Id}\n" +
                    $"Attempt ID: {submission.AttemptId}\n" +
                    $"Status: {submission.Status}\n" +
                    $"Score: {submission.Score} / " +
                    $"{submission.TotalQuestions}\n" +
                    $"Created: {submission.CreatedAtUtc}\n\n";
            }
        }
    }
}