using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Pages;

public partial class ExamPage : ContentPage
{
    private readonly Exam _exam;
    private readonly Attempt _attempt;
    private readonly DatabaseService _databaseService;

    private int _currentQuestionIndex;
    private int _remainingSeconds;

    private IDispatcherTimer? _timer;

    private readonly Dictionary<int, QuestionOption?> _selectedAnswers = new();

    public ExamPage(
        Exam exam,
        Attempt attempt,
        DatabaseService databaseService)
    {
        InitializeComponent();

        _exam = exam;
        _attempt = attempt;
        _databaseService = databaseService;

        _currentQuestionIndex = 0;
        _remainingSeconds = exam.DurationMinutes * 60;

        ExamTitleLabel.Text = exam.Title;

        StartTimer();
        ShowQuestion();
    }

    private void StartTimer()
    {
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += Timer_Tick;
        _timer.Start();

        UpdateTimerLabel();
    }

    private async void Timer_Tick(
        object? sender,
        EventArgs e)
    {
        if (_remainingSeconds <= 0)
        {
            _timer?.Stop();

            await DisplayAlert(
                "Time Over",
                "The exam time has ended.",
                "OK");

            await SubmitExamAsync();
            return;
        }

        _remainingSeconds--;

        UpdateTimerLabel();
    }

    private void UpdateTimerLabel()
    {
        int minutes = _remainingSeconds / 60;
        int seconds = _remainingSeconds % 60;

        TimerLabel.Text =
            $"{minutes:00}:{seconds:00}";
    }

    private void ShowQuestion()
    {
        if (_currentQuestionIndex >=
            _exam.Questions.Count)
        {
            return;
        }

        Question question =
            _exam.Questions[_currentQuestionIndex];

        QuestionLabel.Text =
            $"{_currentQuestionIndex + 1}. {question.Prompt}";

        OptionsLayout.Children.Clear();

        foreach (QuestionOption option in question.Options)
        {
            Button optionButton = new Button
            {
                Text = option.Text,
                BackgroundColor =
                    Color.FromArgb("#FFFFFF"),
                TextColor =
                    Color.FromArgb("#111827"),
                BorderColor =
                    Color.FromArgb("#E5E7EB"),
                BorderWidth = 1,
                CornerRadius = 8,
                HeightRequest = 50
            };

            optionButton.Clicked += (sender, e) =>
            {
                SelectOption(
                    question,
                    option,
                    optionButton);
            };

            OptionsLayout.Children.Add(optionButton);
        }

        if (_currentQuestionIndex ==
            _exam.Questions.Count - 1)
        {
            NextButton.Text = "Submit Exam";
        }
        else
        {
            NextButton.Text = "Next";
        }

        LoadSavedAnswer(question);
    }

    private async void LoadSavedAnswer(
        Question question)
    {
        Answer? savedAnswer =
            await _databaseService.GetAnswerAsync(
                _attempt.Id,
                question.Id);

        if (savedAnswer == null)
        {
            return;
        }

        QuestionOption? matchingOption =
            question.Options.FirstOrDefault(
                option =>
                    option.Text == savedAnswer.Response);

        if (matchingOption == null)
        {
            return;
        }

        _selectedAnswers[_currentQuestionIndex] =
            matchingOption;

        foreach (Button button in
                 OptionsLayout.Children.OfType<Button>())
        {
            if (button.Text == matchingOption.Text)
            {
                SelectButtonAppearance(button);
            }
        }
    }

    private async void SelectOption(
        Question question,
        QuestionOption selectedOption,
        Button selectedButton)
    {
        _selectedAnswers[_currentQuestionIndex] =
            selectedOption;

        foreach (Button button in
                 OptionsLayout.Children.OfType<Button>())
        {
            ResetButtonAppearance(button);
        }

        SelectButtonAppearance(selectedButton);

        var answer = new Answer
        {
            AttemptId = _attempt.Id,
            QuestionId = question.Id,
            Response = selectedOption.Text,
            AnsweredAtUtc = DateTime.UtcNow
        };

        await _databaseService.SaveAnswerAsync(answer);
    }

    private void ResetButtonAppearance(
        Button button)
    {
        button.BackgroundColor =
            Color.FromArgb("#FFFFFF");

        button.TextColor =
            Color.FromArgb("#111827");

        button.BorderColor =
            Color.FromArgb("#E5E7EB");
    }

    private void SelectButtonAppearance(
        Button button)
    {
        button.BackgroundColor =
            Color.FromArgb("#E0F2FE");

        button.TextColor =
            Color.FromArgb("#1479F5");

        button.BorderColor =
            Color.FromArgb("#1479F5");
    }

    private async void NextButton_Clicked(
        object sender,
        EventArgs e)
    {
        if (_currentQuestionIndex ==
            _exam.Questions.Count - 1)
        {
            await SubmitExamAsync();
            return;
        }

        _currentQuestionIndex++;

        ShowQuestion();
    }

    private async Task SubmitExamAsync()
    {
        _timer?.Stop();

        int score = 0;

        foreach (var answer in _selectedAnswers)
        {
            QuestionOption? selectedOption =
                answer.Value;

            if (selectedOption != null &&
                selectedOption.IsCorrect)
            {
                score++;
            }
        }

        int totalQuestions =
            _exam.Questions.Count;

        // Mark the attempt as locally submitted.
        _attempt.Status =
            Domain.Enums.AttemptStatus.SubmittedLocally;

        _attempt.SubmittedAtUtc =
            DateTime.UtcNow;

        await _databaseService.UpdateAttemptAsync(
            _attempt);

        // Save the local submission.
        var submission = new Submission
        {
            AttemptId = _attempt.Id,
            CreatedAtUtc = DateTime.UtcNow,
            Status = "Pending",
            Score = score,
            TotalQuestions = totalQuestions
        };

        await _databaseService.SaveSubmissionAsync(
            submission);

        await Navigation.PushAsync(
            new SubmissionPage(
                _exam,
                _attempt,
                submission));
    }
}