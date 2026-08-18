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
        await DisplayAlertAsync("Authoritative result required",
            "Results are displayed only after Examifo returns a released score.", "OK");
    }
}
