using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Pages;

public partial class SubmissionPage : ContentPage
{
    private readonly Exam _exam;
    private readonly Attempt _attempt;
    private Submission _submission;
    private readonly Services.SubmissionService _submissionService;
    private bool _refreshing;

    public SubmissionPage(
        Exam exam,
        Attempt attempt,
        Submission submission,
        Services.SubmissionService submissionService)
    {
        InitializeComponent();

        _exam = exam;
        _attempt = attempt;
        _submission = submission;
        _submissionService = submissionService;

        ExamTitleLabel.Text =
            exam.Title;

        RenderSubmission();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_submission.ResultStatus is "pending_sync" or "grading")
            await RefreshResultAsync(showFailure: false);
    }

    private void RenderSubmission()
    {
        StatusLabel.Text = _submission.Status;
        DateTime submitted = _submission.AuthoritativeSubmittedAtUtc ?? _submission.CreatedAtUtc;
        SubmittedAtLabel.Text = submitted.ToLocalTime().ToString("dd MMM yyyy, hh:mm tt");
        ViewResultButton.IsVisible = _submission.ResultStatus == "available";
        RefreshResultButton.IsVisible = _submission.ResultStatus != "available";
        (ScoreLabel.Text, ResultExplanationLabel.Text) = _submission.ResultStatus switch
        {
            "grading" => ("Grading", "Examifo is still grading this submission. You can check again shortly."),
            "withheld" => ("Withheld", "The exam owner has not released this result."),
            "available" => ($"{_submission.ScoreObtained:0.##} / {_submission.ScoreTotal:0.##}",
                "This score was returned by Examifo."),
            _ => ("Pending", "The submission must synchronize before its grading status can be checked.")
        };
    }

    private async Task RefreshResultAsync(bool showFailure)
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshResultButton.IsEnabled = false;
        string previousStatus = _submission.ResultStatus;
        RefreshResultButton.Text = "Checking…";
        try
        {
            _submission = await _submissionService.RefreshResultAsync(_attempt.Id);
            RenderSubmission();
            if (showFailure && _submission.ResultStatus == previousStatus)
            {
                string message = _submission.ResultStatus switch
                {
                    "grading" => "Examifo is still grading this submission. No result has been released yet.",
                    "withheld" => "The result is still withheld by the exam owner.",
                    "pending_sync" => "The submission is still waiting to synchronize with Examifo.",
                    _ => "The result status has not changed."
                };
                await DisplayAlertAsync("Result status checked", message, "OK");
            }
        }
        catch (Exception ex)
        {
            if (showFailure)
                await DisplayAlertAsync("Result status unavailable",
                    "Examifo could not be reached. Your submission remains safely stored. " + ex.Message, "OK");
        }
        finally
        {
            _refreshing = false;
            RefreshResultButton.IsEnabled = true;
            RefreshResultButton.Text = "Check result status";
        }
    }

    private async void RefreshResultButton_Clicked(object sender, EventArgs e) =>
        await RefreshResultAsync(showFailure: true);

    private async void ViewResultButton_Clicked(object sender, EventArgs e)
    {
        if (_submission.ResultStatus == "available")
            await Navigation.PushAsync(new ResultPage(_exam, _submission));
    }

    private async void DoneButton_Clicked(
        object sender,
        EventArgs e)
    {
        await Navigation.PopToRootAsync();
    }
}
