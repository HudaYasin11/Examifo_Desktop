using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Pages;

public partial class ReadyPage : ContentPage
{
    private readonly Exam _exam;
    private readonly DatabaseService _databaseService;

    public ReadyPage(
        Exam exam,
        DatabaseService databaseService)
    {
        InitializeComponent();

        _exam = exam;
        _databaseService = databaseService;

        ExamTitleLabel.Text = exam.Title;

        InstructionsLabel.Text =
            $"{exam.Questions.Count} questions • " +
            $"{exam.DurationMinutes} minutes • " +
            $"Maximum {exam.MaxAttempts} attempt(s)";
    }

    private async void StartExamButton_Clicked(
        object sender,
        EventArgs e)
    {
        var attempt = new Attempt
        {
            ExamId = _exam.Id,
            Status = AttemptStatus.InProgress,
            StartedAtUtc = DateTime.UtcNow,
            DeadlineUtc = DateTime.UtcNow.AddMinutes(_exam.DurationMinutes)
        };

        await _databaseService.SaveAttemptAsync(attempt);

        await Navigation.PushAsync(
            new ExamPage(_exam, attempt, _databaseService));
    }
}