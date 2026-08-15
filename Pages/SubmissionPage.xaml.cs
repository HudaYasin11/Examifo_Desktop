using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Pages;

public partial class SubmissionPage : ContentPage
{
    private readonly Exam _exam;
    private readonly Attempt _attempt;
    private readonly Submission _submission;

    public SubmissionPage(
        Exam exam,
        Attempt attempt,
        Submission submission)
    {
        InitializeComponent();

        _exam = exam;
        _attempt = attempt;
        _submission = submission;

        ExamTitleLabel.Text =
            exam.Title;

        ScoreLabel.Text = "Pending";

        StatusLabel.Text =
            submission.Status;

        SubmittedAtLabel.Text =
            submission.CreatedAtUtc
                .ToLocalTime()
                .ToString("dd MMM yyyy, hh:mm tt");
    }

    private async void DoneButton_Clicked(
        object sender,
        EventArgs e)
    {
        await Navigation.PopToRootAsync();
    }
}
