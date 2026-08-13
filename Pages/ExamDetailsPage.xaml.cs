using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Pages;

public partial class ExamDetailsPage : ContentPage
{
    private readonly Exam _exam;

    public ExamDetailsPage(Exam exam)
    {
        InitializeComponent();

        _exam = exam;

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
        await Navigation.PushAsync(
            new ReadyPage(_exam));
    }
}