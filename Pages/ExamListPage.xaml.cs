
using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Pages;

public partial class ExamListPage : ContentPage
{
    private readonly List<Exam> _exams;

    public ExamListPage()
    {
        InitializeComponent();

        ExamCollectionView.SelectionChanged += ExamCollectionView_SelectionChanged;

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
                new ExamDetailsPage(selectedExam));
        }
    }

    private List<Exam> CreateMockExams()
    {
        return new List<Exam>
        {
            new Exam
            {
                Title = "Examifo Demo Exam",
                Description = "Demo examination for testing Examifo Desktop.",
                DurationMinutes = 10,
                MaxAttempts = 1,
                ProctoringEnabled = false,

                Questions = new List<Question>
                {
                    new Question
                    {
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