using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Pages;

public partial class ResultPage : ContentPage
{
    private readonly Exam _exam;
    private readonly int _score;
    private readonly int _totalQuestions;

    public ResultPage(
        Exam exam,
        int score,
        int totalQuestions)
    {
        InitializeComponent();

        _exam = exam;
        _score = score;
        _totalQuestions = totalQuestions;

        ExamTitleLabel.Text = exam.Title;

        ScoreLabel.Text =
            $"{score} / {totalQuestions}";

        double percentage = totalQuestions == 0
            ? 0
            : (double)score / totalQuestions * 100;

        PercentageLabel.Text =
            $"{percentage:0.##}%";

        if (percentage >= 50)
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