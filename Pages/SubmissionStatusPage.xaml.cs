using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Pages;

public partial class SubmissionStatusPage : ContentPage
{
    private readonly Exam _exam;
    private readonly int _score;
    private readonly int _totalQuestions;

    public SubmissionStatusPage(
        Exam exam,
        int score,
        int totalQuestions)
    {
        InitializeComponent();

        _exam = exam;
        _score = score;
        _totalQuestions = totalQuestions;
    }

    private async void ViewResultButton_Clicked(
        object sender,
        EventArgs e)
    {
        await Navigation.PushAsync(
            new ResultPage(
                _exam,
                _score,
                _totalQuestions));
    }
}