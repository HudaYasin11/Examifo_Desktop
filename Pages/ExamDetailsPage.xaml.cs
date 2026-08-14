using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Pages;

public partial class ExamDetailsPage : ContentPage
{
    private readonly Exam _exam;
    private readonly DatabaseService _databaseService;

    public ExamDetailsPage(
        Exam exam,
        DatabaseService databaseService)
    {
        InitializeComponent();

        _exam = exam;
        _databaseService = databaseService;

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
            new ReadyPage(_exam, _databaseService));
    }
}