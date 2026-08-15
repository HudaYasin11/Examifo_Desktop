using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Persistence;
using Examifo_Desktop.Services;

namespace Examifo_Desktop.Pages;

public partial class ExamListPage : ContentPage
{
    private List<Exam> _exams = new();
    private readonly DatabaseService _databaseService;
    private readonly ExamService _examService;
    private readonly AttemptService _attemptService;
    private readonly SubmissionService _submissionService;
    private bool _loaded;

    public ExamListPage(DatabaseService databaseService, ExamService examService,
        AttemptService attemptService, SubmissionService submissionService)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _examService = examService;
        _attemptService = attemptService;
        _submissionService = submissionService;

        ExamCollectionView.SelectionChanged +=
            ExamCollectionView_SelectionChanged;

        ExamCollectionView.ItemsSource = _exams;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
            return;

        _loaded = true;

        try
        {
            _exams = await _examService.GetAvailableExamsAsync();
            try
            {
                await _submissionService.SyncPendingAsync();
            }
            catch (Exception syncException)
            {
                System.Diagnostics.Debug.WriteLine($"Pending sync will retry later: {syncException}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Real exam loading failed: {ex}");
#if DEBUG
            _exams = CreateMockExams();
#else
            _exams = new List<Exam>();
            await DisplayAlertAsync("Unable to load exams",
                "Your assigned exams could not be retrieved. Please try again.", "OK");
#endif
        }

        ExamCollectionView.ItemsSource = _exams;
    }

    private async void ExamCollectionView_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is Exam selectedExam)
        {
            ExamCollectionView.SelectedItem = null;
            try
            {
                Exam preparedExam = selectedExam.Questions.Count > 0
                    ? selectedExam
                    : await _examService.PrepareExamAsync(selectedExam);
                await Navigation.PushAsync(new ExamDetailsPage(
                    preparedExam, _databaseService, _attemptService, _submissionService));
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync("Unable to open exam", ex.Message, "OK");
            }
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
