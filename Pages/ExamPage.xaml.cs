using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Pages;

public partial class ExamPage : ContentPage
{
    private readonly Exam _exam;

    private int _currentQuestionIndex;
    private int _remainingSeconds;

    private IDispatcherTimer? _timer;

    private readonly Dictionary<int, QuestionOption?> _selectedAnswers = new();

    public ExamPage(Exam exam)
    {
        InitializeComponent();

        _exam = exam;
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
    }

    private async void Timer_Tick(object? sender, EventArgs e)
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

        int minutes = _remainingSeconds / 60;
        int seconds = _remainingSeconds % 60;

        TimerLabel.Text = $"{minutes:00}:{seconds:00}";
    }

    private void ShowQuestion()
    {
        if (_currentQuestionIndex >= _exam.Questions.Count)
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
                BackgroundColor = Color.FromArgb("#FFFFFF"),
                TextColor = Color.FromArgb("#111827"),
                BorderColor = Color.FromArgb("#E5E7EB"),
                BorderWidth = 1,
                CornerRadius = 8,
                HeightRequest = 50
            };

            optionButton.Clicked += (sender, e) =>
            {
                SelectOption(option, optionButton);
            };

            OptionsLayout.Children.Add(optionButton);
        }

        // Change Next → Submit on the final question
        if (_currentQuestionIndex ==
            _exam.Questions.Count - 1)
        {
            NextButton.Text = "Submit Exam";
        }
        else
        {
            NextButton.Text = "Next";
        }
    }

    private void SelectOption(
        QuestionOption selectedOption,
        Button selectedButton)
    {
        _selectedAnswers[_currentQuestionIndex] =
            selectedOption;

        foreach (Button button in OptionsLayout.Children
                     .OfType<Button>())
        {
            button.BackgroundColor =
                Color.FromArgb("#FFFFFF");

            button.TextColor =
                Color.FromArgb("#111827");

            button.BorderColor =
                Color.FromArgb("#E5E7EB");
        }

        selectedButton.BackgroundColor =
            Color.FromArgb("#E0F2FE");

        selectedButton.TextColor =
            Color.FromArgb("#1479F5");

        selectedButton.BorderColor =
            Color.FromArgb("#1479F5");
    }

    private async void NextButton_Clicked(
        object sender,
        EventArgs e)
    {
        // LAST QUESTION → SUBMIT
        if (_currentQuestionIndex ==
            _exam.Questions.Count - 1)
        {
            await SubmitExamAsync();
            return;
        }

        // OTHERWISE → NEXT QUESTION
        _currentQuestionIndex++;

        ShowQuestion();
    }

    private async Task SubmitExamAsync()
    {
        _timer?.Stop();

        int score = 0;

        foreach (var answer in _selectedAnswers)
        {
            QuestionOption? selectedOption = answer.Value;

            if (selectedOption != null &&
                selectedOption.IsCorrect)
            {
                score++;
            }
        }

        int totalQuestions = _exam.Questions.Count;

        await Navigation.PushAsync(
            new SubmissionStatusPage(
                _exam,
                score,
                totalQuestions));
    }
}