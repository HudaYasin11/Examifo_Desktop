using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Persistence;
using Examifo_Desktop.Services;

namespace Examifo_Desktop.Pages;

public partial class ReadyPage : ContentPage
{
    private readonly Exam _exam;
    private readonly DatabaseService _databaseService;
    private readonly AttemptService _attemptService;
    private readonly SubmissionService _submissionService;

    public ReadyPage(
        Exam exam,
        DatabaseService databaseService,
        AttemptService attemptService,
        SubmissionService submissionService)
    {
        InitializeComponent();

        _exam = exam;
        _databaseService = databaseService;
        _attemptService = attemptService;
        _submissionService = submissionService;

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
        try
        {
            Attempt attempt = await _attemptService.StartAsync(_exam);
            await Navigation.PushAsync(new ExamPage(
                _exam, attempt, _databaseService, _submissionService));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot start exam", ex.Message, "OK");
        }
    }
}
