using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Pages;

public partial class ReadyPage : ContentPage
{
    private readonly Exam _exam;

    public ReadyPage(Exam exam)
    {
        InitializeComponent();

        _exam = exam;

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
        await Navigation.PushAsync(
            new ExamPage(_exam));
    }
}