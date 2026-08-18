using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Pages;

public partial class ResultPage : ContentPage
{
    private readonly Exam _exam;
    private readonly Submission _submission;

    public ResultPage(
        Exam exam,
        Submission submission)
    {
        InitializeComponent();

        _exam = exam;
        if (submission.ResultStatus != "available" || !submission.ScoreObtained.HasValue
            || !submission.ScoreTotal.HasValue || !submission.Percentage.HasValue
            || !submission.Passed.HasValue)
            throw new InvalidOperationException("Only an authoritative released result can be displayed.");
        _submission = submission;

        ExamTitleLabel.Text = exam.Title;

        ScoreLabel.Text = $"{submission.ScoreObtained:0.##} / {submission.ScoreTotal:0.##}";
        PercentageLabel.Text = $"{submission.Percentage:0.##}%";

        if (submission.Passed.Value)
        {
            ResultMessageLabel.Text = "Congratulations! You passed.";
            ResultMessageLabel.TextColor =
                Color.FromArgb("#08A6B5");
        }
        else
        {
            ResultMessageLabel.Text = "You did not pass this attempt.";
            ResultMessageLabel.TextColor =
                Color.FromArgb("#DC2626");
        }
    }

    private async void BackToExamsButton_Clicked(
        object sender,
        EventArgs e)
    {
        await Navigation.PopToRootAsync();
    }
}
