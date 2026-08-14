using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Pages;

public partial class ExamListPage : ContentPage
{
    private readonly List<Exam> _exams;
    private readonly DatabaseService _databaseService;

    public ExamListPage(DatabaseService databaseService)
    {
        InitializeComponent();

        _databaseService = databaseService;

        ExamCollectionView.SelectionChanged +=
            ExamCollectionView_SelectionChanged;

        _exams = CreateMockExams();

        ExamCollectionView.ItemsSource = _exams;
    }

    private async void ExamCollectionView_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is Exam selectedExam)
        {
            ExamCollectionView.SelectedItem = null;

            await Navigation.PushAsync(
                new ExamDetailsPage(
                    selectedExam,
                    _databaseService));
        }
    }

    private List<Exam> CreateMockExams()
    {
        return new List<Exam>
        {
            new Exam
            {
                Id = Guid.NewGuid(),
                Title = "Examifo Demo Exam",
                Description = "Demo examination for testing Examifo Desktop.",
                DurationMinutes = 10,
                MaxAttempts = 1,
                ProctoringEnabled = false,

                Questions = new List<Question>
                {
                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Prompt = "What is 2 + 2?",
                        QuestionType = QuestionType.SingleChoice,
                        Marks = 1,
                        IsRequired = true,

                        Options = new List<QuestionOption>
                        {
                            new QuestionOption
                            {
                                Text = "3"
                            },

                            new QuestionOption
                            {
                                Text = "4",
                                IsCorrect = true
                            },

                            new QuestionOption
                            {
                                Text = "5"
                            }
                        }
                    },

                    new Question
                    {
                        Id = Guid.NewGuid(),
                        Prompt = "C# is a programming language.",
                        QuestionType = QuestionType.TrueFalse,
                        Marks = 1,
                        IsRequired = true
                    }
                }
            }
        };
    }
}