using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Persistence;
using Examifo_Desktop.Services;

namespace Examifo_Desktop.Pages;

public partial class ExamDetailsPage : ContentPage
{
    private readonly Exam _exam;
    private readonly DatabaseService _databaseService;
    private readonly AttemptService _attemptService;
    private readonly SubmissionService _submissionService;
    private bool _continueInProgress;

    public ExamDetailsPage(
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

        TitleLabel.Text = exam.Title;
        DescriptionLabel.Text = exam.Description;

        DurationLabel.Text =
            $"{exam.DurationMinutes} minutes";

        QuestionsLabel.Text =
            exam.Questions.Count.ToString();

        AttemptsLabel.Text =
            exam.MaxAttempts.ToString();

        ProctoringLabel.Text =
            exam.ProctoringEnabled
                ? "Enabled"
                : "Disabled";
    }

    private async void ContinueButton_Clicked(
        object sender,
        EventArgs e)
    {
        if (_continueInProgress) return;
        _continueInProgress = true;
        ContinueButton.IsEnabled = false;
        ContinueButton.Text = "Preparing offline access…";
        try
        {
            Examifo_Desktop.Domain.Models.Attempt? resumable =
                await _attemptService.GetResumableAttemptAsync(_exam);
            if (resumable is not null)
            {
                await Navigation.PushAsync(new ExamPage(
                    _exam, resumable, _databaseService, _submissionService, _attemptService));
                return;
            }
            await _attemptService.GetOrCreateAuthorizationAsync(_exam);
            await Navigation.PushAsync(
                new ReadyPage(_exam, _databaseService, _attemptService, _submissionService));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Offline access decision failed: {ex}");
            string explanation = await _attemptService.ExplainOfflineAccessFailureAsync(_exam, ex);
            await DisplayAlertAsync("Cannot continue this exam", explanation, "OK");
        }
        finally
        {
            _continueInProgress = false;
            ContinueButton.IsEnabled = true;
            ContinueButton.Text = "Continue";
        }
    }
}
