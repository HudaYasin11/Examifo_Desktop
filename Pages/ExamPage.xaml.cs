using System.Text.Json;
using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Persistence;
using Examifo_Desktop.Services;
using Microsoft.Maui.Graphics;

namespace Examifo_Desktop.Pages;

public partial class ExamPage : ContentPage
{
    private static readonly string[] DefaultCodeLanguages =
        ["plain_text", "csharp", "python", "javascript", "java", "cpp", "sql"];
    private static readonly JsonSerializerOptions AdvancedJsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly Exam _exam;
    private readonly Attempt _attempt;
    private readonly DatabaseService _databaseService;
    private readonly SubmissionService _submissionService;
    private readonly AttemptClock _clock;
    private readonly Dictionary<Guid, HashSet<Guid>> _selectedOptions = new();
    private readonly Dictionary<Guid, string> _textResponses = new();
    private readonly Dictionary<Guid, string> _codeLanguages = new();
    private readonly Dictionary<Guid, Dictionary<string, string>> _documentResponses = new();
    private readonly Dictionary<Guid, List<DrawingStroke>> _drawingResponses = new();
    private readonly HashSet<Guid> _dirtyTextAnswers = [];
    private readonly SemaphoreSlim _textSaveGate = new(1, 1);
    private readonly Dictionary<Guid, Button> _optionButtons = new();
    private int _currentQuestionIndex;
    private int _remainingSeconds;
    private IDispatcherTimer? _timer;
    private bool _initialized;
    private bool _busy;
    private bool _timerTickBusy;
    private DateTimeOffset _lastTimerCheckpointUtc;
    private bool _loadingEditor;
    private CancellationTokenSource? _autosaveCancellation;
    private readonly DrawingCanvasDrawable _drawingDrawable = new();
    private DrawingStroke? _activeStroke;
    private string _drawingColorHex = "#0F172A";
    private float _drawingThickness = 3;
    private readonly Dictionary<string, Button> _drawingColorButtons = new();
    private string _mathInputMode = "Math";

    public ExamPage(Exam exam, Attempt attempt, DatabaseService databaseService,
        SubmissionService submissionService, AttemptService attemptService)
    {
        InitializeComponent();
        _exam = exam;
        _attempt = attempt;
        _databaseService = databaseService;
        _submissionService = submissionService;
        _clock = attemptService.CreateClock(attempt);
        DeterministicExamOrder.Apply(_exam,
            string.IsNullOrWhiteSpace(attempt.ShuffleSeed) ? attempt.Id.ToString("N") : attempt.ShuffleSeed);
        _currentQuestionIndex = Math.Clamp(attempt.CurrentQuestionIndex, 0,
            Math.Max(0, exam.Questions.Count - 1));
        ExamTitleLabel.Text = exam.Title;
        DrawingCanvas.Drawable = _drawingDrawable;
        BuildDrawingColorPalette();
        BuildMathKeyboard();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_initialized)
        {
            _initialized = true;
            await LoadSavedAnswersAsync();
            BuildNavigator();
        }
        SampleAttemptClock();
        StartTimer();
        ShowQuestion();
        if (_attempt.Status == AttemptStatus.InProgress) await RecordVisibilityAsync("exam.view.entered");
    }

    protected override async void OnDisappearing()
    {
        await FlushCurrentDraftAsync();
        await PersistTimerCheckpointAsync();
        _timer?.Stop();
        if (_attempt.Status == AttemptStatus.InProgress) await RecordVisibilityAsync("exam.view.hidden");
        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed()
    {
        if (!ReviewScrollView.IsVisible) return base.OnBackButtonPressed();
        ReviewScrollView.IsVisible = false;
        QuestionScrollView.IsVisible = true;
        return true;
    }

    private async Task RecordVisibilityAsync(string eventType)
    {
        try { await _databaseService.RecordProctoringEventWithOperationAsync(
            _attempt.Id, eventType, _clock.Sample().EffectiveUtcNow.UtcDateTime, "{}"); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }

    private async Task LoadSavedAnswersAsync()
    {
        foreach (Answer answer in await _databaseService.GetAnswersAsync(_attempt.Id))
        {
            if (answer.ResponseFormat is "text" or "essay" or "math")
            {
                _textResponses[answer.QuestionId] = answer.Response;
                continue;
            }
            if (answer.ResponseFormat == "drawing")
            {
                try
                {
                    DrawingResponseDocument? document = JsonSerializer.Deserialize<DrawingResponseDocument>(
                        answer.Response, AdvancedJsonOptions);
                    if (document?.StyledStrokes is { Count: > 0 })
                        _drawingResponses[answer.QuestionId] = document.StyledStrokes.Select(stroke =>
                            new DrawingStroke(stroke.Points.Select(point => new PointF(point.X, point.Y)).ToList(),
                                stroke.ColorHex, stroke.Thickness)).ToList();
                    else if (document?.Strokes is { Count: > 0 })
                        _drawingResponses[answer.QuestionId] = document.Strokes.Select(stroke =>
                            new DrawingStroke(stroke.Select(point => new PointF(point.X, point.Y)).ToList(),
                                "#0F172A", 3)).ToList();
                }
                catch (JsonException) { }
                continue;
            }
            if (answer.ResponseFormat is "multi_part" or "table_grid")
            {
                try
                {
                    Dictionary<string, string>? values = JsonSerializer.Deserialize<Dictionary<string, string>>(answer.Response);
                    if (values is not null) _documentResponses[answer.QuestionId] = values;
                }
                catch (JsonException) { }
                continue;
            }
            if (answer.ResponseFormat == "code")
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(answer.Response);
                    _textResponses[answer.QuestionId] = document.RootElement.GetProperty("submission").GetString() ?? string.Empty;
                    _codeLanguages[answer.QuestionId] = document.RootElement.GetProperty("language").GetString() ?? string.Empty;
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
                {
                    _textResponses[answer.QuestionId] = answer.Response;
                }
                continue;
            }
            HashSet<Guid> ids = [];
            if (answer.SelectedOptionId.HasValue) ids.Add(answer.SelectedOptionId.Value);
            else
            {
                try { foreach (Guid id in JsonSerializer.Deserialize<Guid[]>(answer.Response) ?? []) ids.Add(id); }
                catch (JsonException)
                {
                    Question? question = _exam.Questions.FirstOrDefault(x => x.Id == answer.QuestionId);
                    QuestionOption? legacy = question?.Options.FirstOrDefault(x => x.Text == answer.Response);
                    if (legacy is not null) ids.Add(legacy.Id);
                }
            }
            if (ids.Count > 0) _selectedOptions[answer.QuestionId] = ids;
        }
    }

    private AttemptClockSample SampleAttemptClock()
    {
        AttemptClockSample sample = _clock.Sample();
        _remainingSeconds = Math.Max(0, (int)Math.Ceiling(sample.Remaining.TotalSeconds));
        return sample;
    }

    private void StartTimer()
    {
        _timer?.Stop();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += Timer_Tick;
        _timer.Start();
        UpdateTimerLabel();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_timerTickBusy) return;
        _timerTickBusy = true;
        try
        {
            AttemptClockSample sample = SampleAttemptClock();
            UpdateTimerLabel();
            if (sample.ClockChangeDetected) await RecordClockChangeAsync(sample);
            if (sample.EffectiveUtcNow - _lastTimerCheckpointUtc >= TimeSpan.FromSeconds(15))
                await PersistTimerCheckpointAsync(sample.EffectiveUtcNow);
            if (_remainingSeconds > 0 || _busy) return;
            _timer?.Stop();
            await DisplayAlertAsync("Time over", "The exam time has ended. Your saved answers will be submitted.", "OK");
            await SubmitExamAsync();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer checkpoint failed: {ex}"); }
        finally { _timerTickBusy = false; }
    }

    private async Task RecordClockChangeAsync(AttemptClockSample sample)
    {
        string metadata = JsonSerializer.Serialize(new
        {
            driftSeconds = Math.Round(sample.WallClockDrift.TotalSeconds, 3),
            effectiveUtc = sample.EffectiveUtcNow
        });
        await _databaseService.RecordProctoringEventWithOperationAsync(
            _attempt.Id, "clock.change.detected", sample.EffectiveUtcNow.UtcDateTime, metadata);
    }

    private async Task PersistTimerCheckpointAsync(DateTimeOffset? observedUtc = null)
    {
        if (_attempt.Status != AttemptStatus.InProgress) return;
        DateTimeOffset value = observedUtc ?? _clock.Sample().EffectiveUtcNow;
        await _databaseService.UpdateAttemptProgressAsync(
            _attempt.Id, _currentQuestionIndex, value.UtcDateTime);
        _attempt.LastActivityUtc = value.UtcDateTime;
        _lastTimerCheckpointUtc = value;
    }

    private void UpdateTimerLabel() => TimerLabel.Text =
        $"{_remainingSeconds / 60:00}:{_remainingSeconds % 60:00}";

    private static bool IsObjective(QuestionType type) => type is
        QuestionType.SingleChoice or QuestionType.TrueFalse or QuestionType.MultipleSelect;

    private static bool IsTextual(QuestionType type) => type is
        QuestionType.ShortAnswer or QuestionType.Essay or QuestionType.Coding or QuestionType.Math;

    private static bool IsAdvanced(QuestionType type) => type is
        QuestionType.Drawing or QuestionType.MultiPart or QuestionType.TableGrid;

    private void ShowQuestion()
    {
        if (_exam.Questions.Count == 0) return;
        Question question = _exam.Questions[_currentQuestionIndex];
        QuestionProgressLabel.Text = $"Question {_currentQuestionIndex + 1} of {_exam.Questions.Count}";
        MarksLabel.Text = $"{question.Marks:0.##} mark{(question.Marks == 1 ? "" : "s")}";
        QuestionLabel.Text = question.Prompt;
        OptionsLayout.Children.Clear();
        _optionButtons.Clear();
        OptionsLayout.IsVisible = IsObjective(question.QuestionType);
        TextAnswerLayout.IsVisible = IsTextual(question.QuestionType);
        DrawingAnswerLayout.IsVisible = question.QuestionType == QuestionType.Drawing;
        StructuredAnswerLayout.IsVisible = question.QuestionType == QuestionType.MultiPart;
        TableAnswerLayout.IsVisible = question.QuestionType == QuestionType.TableGrid;
        UnsupportedQuestionLabel.IsVisible = !IsObjective(question.QuestionType)
            && !IsTextual(question.QuestionType) && !IsAdvanced(question.QuestionType);
        UnsupportedQuestionLabel.Text = $"{question.QuestionType} answers will be enabled in the next engine batch.";
        foreach (QuestionOption option in question.Options)
        {
            var button = new Button
            {
                Text = option.Text, BackgroundColor = Colors.White, TextColor = Color.FromArgb("#111827"),
                BorderColor = Color.FromArgb("#E5E7EB"), BorderWidth = 1, CornerRadius = 8,
                MinimumHeightRequest = 50, HorizontalOptions = LayoutOptions.Fill
            };
            button.Clicked += async (_, _) => await SelectOptionAsync(question, option);
            _optionButtons[option.Id] = button;
            OptionsLayout.Children.Add(button);
        }
        ConfigureTextEditor(question);
        ConfigureAdvancedEditor(question);
        RefreshOptionAppearance(question);
        PreviousButton.IsEnabled = _currentQuestionIndex > 0;
        ClearAnswerButton.IsEnabled = HasAnswer(question);
        NextButton.Text = _currentQuestionIndex == _exam.Questions.Count - 1 ? "Review & Submit" : "Next";
        RefreshNavigator();
    }

    private void ConfigureTextEditor(Question question)
    {
        _loadingEditor = true;
        bool code = question.QuestionType == QuestionType.Coding;
        CodeLanguagePicker.IsVisible = code;
        MathKeyboardPanel.IsVisible = question.QuestionType == QuestionType.Math;
        WordCountLabel.IsVisible = question.QuestionType is QuestionType.ShortAnswer or QuestionType.Essay;
        ResponseEditor.MinimumHeightRequest = question.QuestionType == QuestionType.ShortAnswer ? 100 : 220;
        ResponseEditor.Placeholder = code ? "Write code here…" : question.QuestionType switch
        {
            QuestionType.Essay => "Write your essay here…",
            QuestionType.Math => "Enter your equation or mathematical working…",
            _ => "Write your answer here…"
        };
        ResponseEditor.Text = _textResponses.GetValueOrDefault(question.Id, string.Empty);
        ResponseEditor.FontFamily = code ? "Consolas" : null;
        ResponseEditor.BackgroundColor = code ? Color.FromArgb("#111827") : Color.FromArgb("#F8FAFC");
        ResponseEditor.TextColor = code ? Color.FromArgb("#E5E7EB") : Color.FromArgb("#111827");
        if (code)
        {
            string[] languages = GetCodeLanguages(question);
            CodeLanguagePicker.ItemsSource = languages;
            string selected = _codeLanguages.GetValueOrDefault(question.Id, languages.FirstOrDefault() ?? "plain_text");
            CodeLanguagePicker.SelectedIndex = Math.Max(0, Array.IndexOf(languages, selected));
            _codeLanguages[question.Id] = selected;
        }
        SaveStatusLabel.IsVisible = false;
        UpdateWordCount(ResponseEditor.Text);
        _loadingEditor = false;
    }

    private void BuildMathKeyboard(string category = "Basic")
    {
        MathKeyboardLayout.Children.Clear();
        IEnumerable<(string Label, string Insert, int CursorBack)> keys = category switch
        {
            "Symbols" => [("sin", "sin()", 1), ("cos", "cos()", 1), ("tan", "tan()", 1),
                ("ln", "ln()", 1), ("log", "log()", 1), ("abs", "abs()", 1),
                ("=", "=", 0), ("≠", "≠", 0), ("<", "<", 0), (">", ">", 0),
                ("≤", "≤", 0), ("≥", "≥", 0), ("≈", "≈", 0), ("∝", "∝", 0), ("∈", "∈", 0),
                ("∉", "∉", 0), ("⊂", "⊂", 0), ("⊆", "⊆", 0), ("∪", "∪", 0), ("∩", "∩", 0),
                ("∀", "∀", 0), ("∃", "∃", 0), ("¬", "¬", 0), ("⇒", "⇒", 0), ("⇔", "⇔", 0),
                ("∞", "∞", 0), ("∠", "∠", 0), ("⊥", "⊥", 0)],
            "Algebra" => [("x²", "^2", 0), ("xⁿ", "^{}", 1), ("√x", "√()", 1),
                ("ⁿ√x", "root(,)", 2), ("a/b", "frac(,)", 2), ("|x|", "abs()", 1),
                ("log", "log()", 1), ("logₐ", "log_base(,)", 2), ("ln", "ln()", 1),
                ("eˣ", "e^{}", 1), ("10ˣ", "10^{}", 1), ("±", "±", 0), ("∞", "∞", 0)],
            "Greek" => [("α", "α", 0), ("β", "β", 0), ("γ", "γ", 0), ("δ", "δ", 0),
                ("Δ", "Δ", 0), ("ε", "ε", 0), ("θ", "θ", 0), ("λ", "λ", 0), ("μ", "μ", 0),
                ("π", "π", 0), ("ρ", "ρ", 0), ("σ", "σ", 0), ("Σ", "Σ", 0), ("φ", "φ", 0), ("ω", "ω", 0)],
            "Letters" => "1234567890qwertyuiopasdfghjklzxcvbnm".Select(character =>
                (character.ToString(), character.ToString(), 0)),
            "Calculus" => [("∫", "∫() dx", 4), ("∫ᵃᵇ", "∫_[a]^[b]() dx", 4),
                ("d/dx", "d/dx()", 1), ("∂/∂x", "∂/∂x()", 1), ("Σ", "Σ_[i=1]^[n]()", 1),
                ("Π", "Π_[i=1]^[n]()", 1), ("lim", "lim_[x→]()", 1), ("∇", "∇", 0),
                ("dx", "dx", 0), ("dy", "dy", 0), ("∂", "∂", 0)],
            "Matrices" => [("2×2", "[[,],[,]]", 6), ("3×3", "[[,,],[,,],[,,]]", 14),
                ("vector", "<,,>", 3), ("det", "det([[]])", 3), ("transpose", "^T", 0),
                ("inverse", "^-1", 0), ("dot", "·", 0), ("cross", "×", 0)],
            _ => [("x", "x", 0), ("n", "n", 0), ("7", "7", 0), ("8", "8", 0),
                ("9", "9", 0), ("÷", "÷", 0), ("<", "<", 0), (">", ">", 0), ("4", "4", 0),
                ("5", "5", 0), ("6", "6", 0), ("×", "×", 0), ("(", "(", 0), (")", ")", 0),
                ("1", "1", 0), ("2", "2", 0), ("3", "3", 0), ("−", "−", 0), ("0", "0", 0),
                (".", ".", 0), ("=", "=", 0), ("+", "+", 0), ("←", "←", 0), ("→", "→", 0)]
        };
        foreach ((string label, string insertion, int cursorBack) in keys)
        {
            var button = new Button
            {
                Text = label, MinimumWidthRequest = 58, HeightRequest = 44, Margin = new Thickness(2),
                Padding = new Thickness(4), BackgroundColor = Color.FromArgb("#F8FAFC"),
                TextColor = Color.FromArgb("#0F172A"), FontSize = 16
            };
            button.Clicked += (_, _) => InsertMathSymbol(insertion, cursorBack);
            MathKeyboardLayout.Children.Add(button);
        }
    }

    private void MathCategoryButton_Clicked(object sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string category }) BuildMathKeyboard(category);
    }

    private async void MathToolsButton_Clicked(object sender, EventArgs e)
    {
        string? action = await DisplayActionSheetAsync("Equation tools", "Cancel", null,
            "Insert Matrix", "Calculus", "Mode", "Font Style", "Text Color", "Background Color",
            "Cut", "Copy", "Paste", "Select All");
        switch (action)
        {
            case "Insert Matrix": await ShowMatrixMenuAsync(); break;
            case "Calculus": await ShowCalculusMenuAsync(); break;
            case "Mode": await ShowMathModeMenuAsync(); break;
            case "Font Style": await ShowMathFontMenuAsync(); break;
            case "Text Color": await ShowMathColorMenuAsync(false); break;
            case "Background Color": await ShowMathColorMenuAsync(true); break;
            case "Cut": await CutMathSelectionAsync(); break;
            case "Copy": await CopyMathSelectionAsync(); break;
            case "Paste": await PasteMathSelectionAsync(); break;
            case "Select All": ResponseEditor.CursorPosition = 0; ResponseEditor.SelectionLength =
                    (ResponseEditor.Text ?? string.Empty).Length; break;
        }
    }

    private async Task ShowMatrixMenuAsync()
    {
        string? size = await DisplayActionSheetAsync("Insert matrix", "Cancel", null,
            "2 × 2", "2 × 3", "3 × 2", "3 × 3", "4 × 4");
        string? template = size switch
        {
            "2 × 2" => "[[,],[,]]", "2 × 3" => "[[,,],[,,]]", "3 × 2" => "[[,],[,],[,]]",
            "3 × 3" => "[[,,],[,,],[,,]]", "4 × 4" => "[[,,,],[,,,],[,,,],[,,,]]", _ => null
        };
        if (template is not null) InsertMathSymbol(template, Math.Max(1, template.Length - 2));
    }

    private async Task ShowCalculusMenuAsync()
    {
        string? item = await DisplayActionSheetAsync("Calculus and advanced math", "Cancel", null,
            "Absolute Value", "Nth Root", "Logarithm base a", "Derivative", "Nth derivative",
            "Integral", "Sum", "Product", "Modulus", "Argument", "Real Part", "Imaginary Part", "Conjugate");
        (string Text, int Back)? insertion = item switch
        {
            "Absolute Value" => ("abs()", 1), "Nth Root" => ("root(,)", 2),
            "Logarithm base a" => ("log_base(,)", 2), "Derivative" => ("d/dx()", 1),
            "Nth derivative" => ("d^n/dx^n()", 1), "Integral" => ("∫_[a]^[b]() dx", 4),
            "Sum" => ("Σ_[i=1]^[n]()", 1), "Product" => ("Π_[i=1]^[n]()", 1),
            "Modulus" => ("|z|", 1), "Argument" => ("arg()", 1), "Real Part" => ("Re()", 1),
            "Imaginary Part" => ("Im()", 1), "Conjugate" => ("conj()", 1), _ => null
        };
        if (insertion is { } value) InsertMathSymbol(value.Text, value.Back);
    }

    private async Task ShowMathModeMenuAsync()
    {
        string? mode = await DisplayActionSheetAsync("Input mode", "Cancel", null, "Math", "Text", "LaTeX");
        if (mode is not ("Math" or "Text" or "LaTeX")) return;
        _mathInputMode = mode;
        MathModeLabel.Text = $"Mode: {_mathInputMode}";
    }

    private async Task ShowMathFontMenuAsync()
    {
        string? style = await DisplayActionSheetAsync("Font style", "Cancel", null,
            "Roman Upright", "Bold", "Italic");
        switch (style)
        {
            case "Roman Upright": WrapMathSelection("\\mathrm{", "}"); break;
            case "Bold": WrapMathSelection("\\mathbf{", "}"); break;
            case "Italic": WrapMathSelection("\\mathit{", "}"); break;
        }
    }

    private async Task ShowMathColorMenuAsync(bool background)
    {
        string? color = await DisplayActionSheetAsync(background ? "Background color" : "Text color",
            "Cancel", null, "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Black", "Gray", "White");
        if (color is null or "Cancel") return;
        string name = color.ToLowerInvariant();
        WrapMathSelection(background ? $"\\colorbox{{{name}}}{{" : $"\\color{{{name}}}{{", "}");
    }

    private void WrapMathSelection(string prefix, string suffix)
    {
        string text = ResponseEditor.Text ?? string.Empty;
        int start = Math.Clamp(ResponseEditor.CursorPosition, 0, text.Length);
        int length = Math.Clamp(ResponseEditor.SelectionLength, 0, text.Length - start);
        string selected = length > 0 ? text.Substring(start, length) : "value";
        string replacement = prefix + selected + suffix;
        ResponseEditor.Text = text.Remove(start, length).Insert(start, replacement);
        ResponseEditor.CursorPosition = length > 0 ? start + replacement.Length : start + prefix.Length;
        ResponseEditor.SelectionLength = length > 0 ? 0 : selected.Length;
        ResponseEditor.Focus();
    }

    private async Task CopyMathSelectionAsync()
    {
        string text = ResponseEditor.Text ?? string.Empty;
        int start = Math.Clamp(ResponseEditor.CursorPosition, 0, text.Length);
        int length = Math.Clamp(ResponseEditor.SelectionLength, 0, text.Length - start);
        if (length > 0) await Clipboard.Default.SetTextAsync(text.Substring(start, length));
    }

    private async Task CutMathSelectionAsync()
    {
        string text = ResponseEditor.Text ?? string.Empty;
        int start = Math.Clamp(ResponseEditor.CursorPosition, 0, text.Length);
        int length = Math.Clamp(ResponseEditor.SelectionLength, 0, text.Length - start);
        if (length == 0) return;
        await Clipboard.Default.SetTextAsync(text.Substring(start, length));
        ResponseEditor.Text = text.Remove(start, length);
        ResponseEditor.CursorPosition = start;
    }

    private async Task PasteMathSelectionAsync()
    {
        string? pasted = await Clipboard.Default.GetTextAsync();
        if (pasted is null) return;
        string text = ResponseEditor.Text ?? string.Empty;
        int start = Math.Clamp(ResponseEditor.CursorPosition, 0, text.Length);
        int length = Math.Clamp(ResponseEditor.SelectionLength, 0, text.Length - start);
        ResponseEditor.Text = text.Remove(start, length).Insert(start, pasted);
        ResponseEditor.CursorPosition = start + pasted.Length;
    }

    private void InsertMathSymbol(string insertion, int cursorBack = 0)
    {
        string text = ResponseEditor.Text ?? string.Empty;
        int cursor = Math.Clamp(ResponseEditor.CursorPosition, 0, text.Length);
        ResponseEditor.Text = text.Insert(cursor, insertion);
        ResponseEditor.CursorPosition = Math.Max(cursor, cursor + insertion.Length - cursorBack);
        ResponseEditor.Focus();
    }

    private void UpdateWordCount(string? text)
    {
        int count = string.IsNullOrWhiteSpace(text)
            ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        WordCountLabel.Text = $"{count} word{(count == 1 ? string.Empty : "s")}";
    }

    private void BuildDrawingColorPalette()
    {
        foreach ((string name, string hex) in new[]
        {
            ("Black", "#0F172A"), ("Blue", "#2563EB"), ("Red", "#DC2626"),
            ("Green", "#16A34A"), ("Purple", "#7C3AED"), ("Orange", "#EA580C")
        })
        {
            var button = new Button
            {
                AutomationId = $"DrawingColor{name}", WidthRequest = 34, HeightRequest = 34,
                CornerRadius = 17, Padding = 0, BackgroundColor = Color.FromArgb(hex),
                BorderColor = Colors.Transparent, BorderWidth = 3
            };
            button.Clicked += (_, _) => SelectDrawingColor(hex);
            _drawingColorButtons[hex] = button;
            DrawingColorPalette.Children.Add(button);
        }
        RefreshDrawingColorSelection();
    }

    private void SelectDrawingColor(string hex)
    {
        _drawingColorHex = hex;
        RefreshDrawingColorSelection();
    }

    private void RefreshDrawingColorSelection()
    {
        foreach ((string hex, Button button) in _drawingColorButtons)
        {
            bool selected = string.Equals(hex, _drawingColorHex, StringComparison.OrdinalIgnoreCase);
            button.BorderColor = selected ? Color.FromArgb("#FBBF24") : Colors.Transparent;
            button.Scale = selected ? 1.12 : 1;
            SemanticProperties.SetDescription(button, selected ? "Selected drawing color" : "Drawing color");
        }
    }

    private void DrawingThicknessSlider_ValueChanged(object sender, ValueChangedEventArgs e) =>
        _drawingThickness = (float)e.NewValue;

    private void ClearDrawingButton_Clicked(object sender, EventArgs e)
    {
        if (_busy || _exam.Questions.Count == 0) return;
        Question question = _exam.Questions[_currentQuestionIndex];
        if (question.QuestionType != QuestionType.Drawing) return;
        _drawingDrawable.Clear();
        _drawingResponses.Remove(question.Id);
        DrawingCanvas.Invalidate();
        MarkAdvancedDirty(question, DrawingSaveStatusLabel);
        ClearAnswerButton.IsEnabled = false;
        RefreshNavigator();
    }

    private static string[] GetCodeLanguages(Question question)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(question.SettingsJson))
            {
                using JsonDocument document = JsonDocument.Parse(question.SettingsJson);
                string[] values = ReadCodeLanguages(document.RootElement, 0);
                if (values.Length > 0) return values;
            }
        }
        catch (JsonException) { }
        return DefaultCodeLanguages;
    }

    private static string[] ReadCodeLanguages(JsonElement root, int depth)
    {
        if (depth > 2) return [];
        if (root.ValueKind == JsonValueKind.String)
        {
            string? value = root.GetString();
            if (string.IsNullOrWhiteSpace(value)) return [];
            string trimmed = value.Trim();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('[') || trimmed.StartsWith('"'))
            {
                try
                {
                    using JsonDocument nested = JsonDocument.Parse(trimmed);
                    return ReadCodeLanguages(nested.RootElement, depth + 1);
                }
                catch (JsonException) { }
            }
            return [value];
        }
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().Select(ReadLanguageValue)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct().ToArray();
        if (root.ValueKind != JsonValueKind.Object) return [];
        if (root.TryGetProperty("languages", out JsonElement languages))
            return ReadCodeLanguages(languages, depth + 1);
        if (root.TryGetProperty("language", out JsonElement language))
        {
            string? value = ReadLanguageValue(language);
            if (!string.IsNullOrWhiteSpace(value)) return [value];
        }
        return [];
    }

    private static string? ReadLanguageValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString();
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (string property in new[] { "id", "value", "name", "language" })
            if (element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private bool HasAnswer(Question question) => question.QuestionType switch
    {
        QuestionType.SingleChoice or QuestionType.TrueFalse or QuestionType.MultipleSelect =>
            _selectedOptions.TryGetValue(question.Id, out HashSet<Guid>? ids) && ids.Count > 0,
        QuestionType.ShortAnswer or QuestionType.Essay or QuestionType.Coding or QuestionType.Math =>
            _textResponses.TryGetValue(question.Id, out string? response) && !string.IsNullOrWhiteSpace(response),
        QuestionType.Drawing => _drawingResponses.TryGetValue(question.Id, out List<DrawingStroke>? strokes)
            && strokes.Any(x => x.Points.Count > 0),
        QuestionType.MultiPart or QuestionType.TableGrid =>
            _documentResponses.TryGetValue(question.Id, out Dictionary<string, string>? values)
            && values.Values.Any(x => !string.IsNullOrWhiteSpace(x)),
        _ => false
    };

    private async void ResponseEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingEditor || _exam.Questions.Count == 0) return;
        Question question = _exam.Questions[_currentQuestionIndex];
        _textResponses[question.Id] = e.NewTextValue ?? string.Empty;
        UpdateWordCount(e.NewTextValue);
        _dirtyTextAnswers.Add(question.Id);
        SaveStatusLabel.Text = "Saving…";
        SaveStatusLabel.TextColor = Color.FromArgb("#64748B");
        SaveStatusLabel.IsVisible = true;
        ClearAnswerButton.IsEnabled = !string.IsNullOrWhiteSpace(e.NewTextValue);
        RefreshNavigator();
        _autosaveCancellation?.Cancel();
        _autosaveCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(700, _autosaveCancellation.Token);
            await SaveTextAnswerAsync(question);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SaveStatusLabel.Text = "Not saved — retrying when you leave this question";
            SaveStatusLabel.TextColor = Color.FromArgb("#B91C1C");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async void CodeLanguagePicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (_loadingEditor || _exam.Questions.Count == 0 || CodeLanguagePicker.SelectedItem is not string language) return;
        Question question = _exam.Questions[_currentQuestionIndex];
        _codeLanguages[question.Id] = language;
        _dirtyTextAnswers.Add(question.Id);
        if (_textResponses.TryGetValue(question.Id, out string? text) && !string.IsNullOrWhiteSpace(text))
            await SaveTextAnswerAsync(question);
    }

    private async Task SaveTextAnswerAsync(Question question)
    {
        if (_attempt.Status != AttemptStatus.InProgress || !_dirtyTextAnswers.Contains(question.Id)) return;
        await _textSaveGate.WaitAsync();
        try
        {
        string response = _textResponses.GetValueOrDefault(question.Id, string.Empty);
        if (string.IsNullOrWhiteSpace(response))
        {
            await _databaseService.ClearAnswerWithOperationAsync(_attempt, question.Id,
                question.ExamQuestionId, _clock.Sample().EffectiveUtcNow.UtcDateTime);
            SaveStatusLabel.Text = "Answer cleared";
        }
        else
        {
            Answer? existing = await _databaseService.GetAnswerAsync(_attempt.Id, question.Id);
            string format = question.QuestionType switch
            {
                QuestionType.Essay => "essay",
                QuestionType.Coding => "code",
                QuestionType.Math => "math",
                _ => "text"
            };
            string storedResponse = format == "code"
                ? JsonSerializer.Serialize(new
                {
                    language = _codeLanguages.GetValueOrDefault(question.Id, "plain_text"),
                    submission = response
                })
                : response;
            await _databaseService.SaveAnswerWithOperationAsync(_attempt, new Answer
            {
                Id = existing?.Id ?? Guid.NewGuid(), AttemptId = _attempt.Id,
                QuestionId = question.Id, ExamQuestionId = question.ExamQuestionId,
                ResponseFormat = format, Response = storedResponse,
                AnsweredAtUtc = _clock.Sample().EffectiveUtcNow.UtcDateTime
            });
            SaveStatusLabel.Text = "Saved locally";
        }
        SaveStatusLabel.TextColor = Color.FromArgb("#047857");
        SaveStatusLabel.IsVisible = true;
        _dirtyTextAnswers.Remove(question.Id);
        }
        finally { _textSaveGate.Release(); }
    }

    private async Task FlushCurrentDraftAsync()
    {
        _autosaveCancellation?.Cancel();
        if (_exam.Questions.Count == 0 || _attempt.Status != AttemptStatus.InProgress) return;
        Question question = _exam.Questions[_currentQuestionIndex];
        if (IsTextual(question.QuestionType)) await SaveTextAnswerAsync(question);
        else if (IsAdvanced(question.QuestionType) && _dirtyTextAnswers.Contains(question.Id))
            await SaveAdvancedAnswerAsync(question);
    }

    private void ConfigureAdvancedEditor(Question question)
    {
        _loadingEditor = true;
        StructuredFieldsLayout.Children.Clear();
        TableFieldsGrid.Children.Clear();
        TableFieldsGrid.RowDefinitions.Clear();
        TableFieldsGrid.ColumnDefinitions.Clear();
        DrawingSaveStatusLabel.IsVisible = StructuredSaveStatusLabel.IsVisible =
            TableSaveStatusLabel.IsVisible = false;

        if (question.QuestionType == QuestionType.Drawing)
        {
            _drawingDrawable.Clear();
            if (_drawingResponses.TryGetValue(question.Id, out List<DrawingStroke>? strokes))
                foreach (DrawingStroke stroke in strokes)
                    _drawingDrawable.Strokes.Add(new DrawingStroke(
                        stroke.Points.Select(x => new PointF(x.X, x.Y)).ToList(),
                        stroke.ColorHex, stroke.Thickness));
            DrawingCanvas.Invalidate();
        }
        else if (question.QuestionType == QuestionType.MultiPart)
        {
            Dictionary<string, string> values = GetDocumentValues(question.Id);
            foreach ((string key, string label) in ReadMultipartFields(question))
            {
                StructuredFieldsLayout.Children.Add(new Label
                {
                    Text = label, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#334155")
                });
                var editor = CreateDocumentEditor(values.GetValueOrDefault(key, string.Empty), 90);
                editor.TextChanged += (_, e) => DocumentFieldChanged(question, key, e.NewTextValue,
                    StructuredSaveStatusLabel);
                StructuredFieldsLayout.Children.Add(editor);
            }
        }
        else if (question.QuestionType == QuestionType.TableGrid)
        {
            Dictionary<string, string> values = GetDocumentValues(question.Id);
            (string[] rows, string[] columns) = ReadTableShape(question);
            for (int column = 0; column <= columns.Length; column++)
                TableFieldsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            for (int row = 0; row <= rows.Length; row++)
                TableFieldsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddTableLabel(string.Empty, 0, 0);
            for (int column = 0; column < columns.Length; column++) AddTableLabel(columns[column], column + 1, 0);
            for (int row = 0; row < rows.Length; row++)
            {
                AddTableLabel(rows[row], 0, row + 1);
                for (int column = 0; column < columns.Length; column++)
                {
                    string key = $"{row}:{column}";
                    var editor = CreateDocumentEditor(values.GetValueOrDefault(key, string.Empty), 48);
                    editor.TextChanged += (_, e) => DocumentFieldChanged(question, key, e.NewTextValue,
                        TableSaveStatusLabel);
                    TableFieldsGrid.Add(editor, column + 1, row + 1);
                }
            }
        }
        _loadingEditor = false;
    }

    private Dictionary<string, string> GetDocumentValues(Guid questionId)
    {
        if (!_documentResponses.TryGetValue(questionId, out Dictionary<string, string>? values))
            _documentResponses[questionId] = values = new Dictionary<string, string>();
        return values;
    }

    private static Editor CreateDocumentEditor(string text, double height) => new()
    {
        Text = text, MinimumHeightRequest = height, AutoSize = EditorAutoSizeOption.TextChanges,
        BackgroundColor = Color.FromArgb("#F8FAFC"), TextColor = Color.FromArgb("#111827")
    };

    private void AddTableLabel(string text, int column, int row)
    {
        var label = new Label
        {
            Text = text, Padding = 8, FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#334155"), VerticalTextAlignment = TextAlignment.Center
        };
        TableFieldsGrid.Add(label, column, row);
    }

    private void DocumentFieldChanged(Question question, string key, string? text, Label statusLabel)
    {
        if (_loadingEditor) return;
        GetDocumentValues(question.Id)[key] = text ?? string.Empty;
        MarkAdvancedDirty(question, statusLabel);
    }

    private void MarkAdvancedDirty(Question question, Label statusLabel)
    {
        _dirtyTextAnswers.Add(question.Id);
        statusLabel.Text = "Saving…";
        statusLabel.TextColor = Color.FromArgb("#64748B");
        statusLabel.IsVisible = true;
        ClearAnswerButton.IsEnabled = HasAnswer(question);
        RefreshNavigator();
        _autosaveCancellation?.Cancel();
        _autosaveCancellation = new CancellationTokenSource();
        _ = SaveAdvancedAfterDelayAsync(question, statusLabel, _autosaveCancellation.Token);
    }

    private async Task SaveAdvancedAfterDelayAsync(Question question, Label statusLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(700, cancellationToken);
            await SaveAdvancedAnswerAsync(question);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            statusLabel.Text = "Not saved — retrying when you leave this question";
            statusLabel.TextColor = Color.FromArgb("#B91C1C");
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private async Task SaveAdvancedAnswerAsync(Question question)
    {
        if (_attempt.Status != AttemptStatus.InProgress || !_dirtyTextAnswers.Contains(question.Id)) return;
        await _textSaveGate.WaitAsync();
        try
        {
            string response;
            string format;
            Label statusLabel;
            if (question.QuestionType == QuestionType.Drawing)
            {
                format = "drawing";
                statusLabel = DrawingSaveStatusLabel;
                response = JsonSerializer.Serialize(new DrawingResponseDocument([], 
                    _drawingResponses.GetValueOrDefault(question.Id, [])
                        .Select(stroke => new DrawingStrokeDocument(
                            stroke.Points.Select(point => new DrawingPoint(point.X, point.Y)).ToList(),
                            stroke.ColorHex, stroke.Thickness)).ToList()),
                    AdvancedJsonOptions);
            }
            else
            {
                format = question.QuestionType == QuestionType.MultiPart ? "multi_part" : "table_grid";
                statusLabel = question.QuestionType == QuestionType.MultiPart
                    ? StructuredSaveStatusLabel : TableSaveStatusLabel;
                response = JsonSerializer.Serialize(GetDocumentValues(question.Id));
            }

            if (!HasAnswer(question))
            {
                await _databaseService.ClearAnswerWithOperationAsync(_attempt, question.Id,
                    question.ExamQuestionId, _clock.Sample().EffectiveUtcNow.UtcDateTime);
                statusLabel.Text = "Answer cleared";
            }
            else
            {
                Answer? existing = await _databaseService.GetAnswerAsync(_attempt.Id, question.Id);
                await _databaseService.SaveAnswerWithOperationAsync(_attempt, new Answer
                {
                    Id = existing?.Id ?? Guid.NewGuid(), AttemptId = _attempt.Id,
                    QuestionId = question.Id, ExamQuestionId = question.ExamQuestionId,
                    ResponseFormat = format, Response = response,
                    AnsweredAtUtc = _clock.Sample().EffectiveUtcNow.UtcDateTime
                });
                statusLabel.Text = "Saved locally";
            }
            statusLabel.TextColor = Color.FromArgb("#047857");
            statusLabel.IsVisible = true;
            _dirtyTextAnswers.Remove(question.Id);
        }
        finally { _textSaveGate.Release(); }
    }

    private void DrawingCanvas_StartInteraction(object? sender, TouchEventArgs e)
    {
        if (_busy || _exam.Questions.Count == 0) return;
        _activeStroke = new DrawingStroke([], _drawingColorHex, _drawingThickness);
        _drawingDrawable.Strokes.Add(_activeStroke);
        AddDrawingTouches(e);
    }

    private void DrawingCanvas_DragInteraction(object? sender, TouchEventArgs e) => AddDrawingTouches(e);

    private void DrawingCanvas_EndInteraction(object? sender, TouchEventArgs e)
    {
        AddDrawingTouches(e);
        if (_exam.Questions.Count == 0 || _activeStroke is null) return;
        Question question = _exam.Questions[_currentQuestionIndex];
        _drawingResponses[question.Id] = _drawingDrawable.Strokes
            .Select(stroke => new DrawingStroke(stroke.Points.Select(x => new PointF(x.X, x.Y)).ToList(),
                stroke.ColorHex, stroke.Thickness)).ToList();
        _activeStroke = null;
        MarkAdvancedDirty(question, DrawingSaveStatusLabel);
    }

    private void AddDrawingTouches(TouchEventArgs e)
    {
        if (_activeStroke is null || DrawingCanvas.Width <= 0 || DrawingCanvas.Height <= 0) return;
        foreach (PointF touch in e.Touches)
            _activeStroke.Points.Add(new PointF(
                Math.Clamp(touch.X / (float)DrawingCanvas.Width, 0, 1),
                Math.Clamp(touch.Y / (float)DrawingCanvas.Height, 0, 1)));
        DrawingCanvas.Invalidate();
    }

    private static IReadOnlyList<(string Key, string Label)> ReadMultipartFields(Question question)
    {
        JsonElement root = ReadSettings(question.SettingsJson);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("parts", out JsonElement parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            var result = new List<(string, string)>();
            int index = 0;
            foreach (JsonElement part in parts.EnumerateArray())
            {
                string key = ReadSettingText(part, "id", "key") ?? $"part-{index + 1}";
                string label = ReadSettingText(part, "prompt", "label", "title") ?? $"Part {index + 1}";
                result.Add((key, label));
                index++;
            }
            if (result.Count > 0) return result;
        }
        return [("response", "Response")];
    }

    private static (string[] Rows, string[] Columns) ReadTableShape(Question question)
    {
        JsonElement root = ReadSettings(question.SettingsJson);
        string[] rows = ReadSettingArray(root, "rows", "rowLabels");
        string[] columns = ReadSettingArray(root, "columns", "columnLabels");
        return (rows.Length > 0 ? rows : ["Row 1", "Row 2"],
            columns.Length > 0 ? columns : ["Column 1", "Column 2"]);
    }

    private static JsonElement ReadSettings(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement.Clone();
            if (root.ValueKind == JsonValueKind.String && root.GetString() is { } nested)
            {
                using JsonDocument nestedDocument = JsonDocument.Parse(nested);
                return nestedDocument.RootElement.Clone();
            }
            return root;
        }
        catch (JsonException) { return default; }
    }

    private static string[] ReadSettingArray(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return [];
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement array) || array.ValueKind != JsonValueKind.Array) continue;
            return array.EnumerateArray().Select((item, index) =>
                    item.ValueKind == JsonValueKind.String ? item.GetString()
                    : ReadSettingText(item, "label", "title", "name", "id") ?? $"Item {index + 1}")
                .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
        }
        return [];
    }

    private static string? ReadSettingText(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (string name in names)
            if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private async Task SelectOptionAsync(Question question, QuestionOption option)
    {
        if (_busy || !IsObjective(question.QuestionType)) return;
        _busy = true;
        try
        {
            if (!_selectedOptions.TryGetValue(question.Id, out HashSet<Guid>? ids))
                _selectedOptions[question.Id] = ids = [];
            if (question.QuestionType == QuestionType.MultipleSelect)
            {
                if (!ids.Add(option.Id)) ids.Remove(option.Id);
            }
            else { ids.Clear(); ids.Add(option.Id); }
            if (ids.Count == 0)
            {
                _selectedOptions.Remove(question.Id);
                await _databaseService.ClearAnswerWithOperationAsync(_attempt, question.Id,
                    question.ExamQuestionId, _clock.Sample().EffectiveUtcNow.UtcDateTime);
            }
            else
            {
                Guid[] ordered = ids.OrderBy(x => x).ToArray();
                Answer? existing = await _databaseService.GetAnswerAsync(_attempt.Id, question.Id);
                await _databaseService.SaveAnswerWithOperationAsync(_attempt, new Answer
                {
                    Id = existing?.Id ?? Guid.NewGuid(), AttemptId = _attempt.Id,
                    QuestionId = question.Id, ExamQuestionId = question.ExamQuestionId,
                    SelectedOptionId = question.QuestionType == QuestionType.MultipleSelect ? null : ordered[0],
                    ResponseFormat = "selected_options", Response = JsonSerializer.Serialize(ordered),
                    AnsweredAtUtc = _clock.Sample().EffectiveUtcNow.UtcDateTime
                });
            }
            RefreshOptionAppearance(question);
            ClearAnswerButton.IsEnabled = ids.Count > 0;
            RefreshNavigator();
        }
        catch (Exception ex) { await DisplayAlertAsync("Answer not saved", ex.Message, "OK"); }
        finally { _busy = false; }
    }

    private void RefreshOptionAppearance(Question question)
    {
        _selectedOptions.TryGetValue(question.Id, out HashSet<Guid>? ids);
        foreach ((Guid id, Button button) in _optionButtons)
        {
            bool selected = ids?.Contains(id) == true;
            button.BackgroundColor = selected ? Color.FromArgb("#E0F2FE") : Colors.White;
            button.TextColor = selected ? Color.FromArgb("#1479F5") : Color.FromArgb("#111827");
            button.BorderColor = selected ? Color.FromArgb("#1479F5") : Color.FromArgb("#E5E7EB");
        }
    }

    private void BuildNavigator()
    {
        QuestionNavigatorLayout.Children.Clear();
        for (int i = 0; i < _exam.Questions.Count; i++)
        {
            int target = i;
            var button = new Button { Text = (i + 1).ToString(), WidthRequest = 42, HeightRequest = 42,
                CornerRadius = 21, Padding = 0, CommandParameter = i };
            button.Clicked += async (_, _) => await NavigateToAsync(target);
            QuestionNavigatorLayout.Children.Add(button);
        }
        RefreshNavigator();
    }

    private void RefreshNavigator()
    {
        for (int i = 0; i < QuestionNavigatorLayout.Children.Count; i++)
        {
            if (QuestionNavigatorLayout.Children[i] is not Button button) continue;
            bool current = i == _currentQuestionIndex;
            bool answered = HasAnswer(_exam.Questions[i]);
            button.BackgroundColor = current ? Color.FromArgb("#1479F5") :
                answered ? Color.FromArgb("#D1FAE5") : Color.FromArgb("#E2E8F0");
            button.TextColor = current ? Colors.White : Color.FromArgb("#1E293B");
        }
    }

    private async Task NavigateToAsync(int index)
    {
        if (_busy || index < 0 || index >= _exam.Questions.Count || index == _currentQuestionIndex) return;
        await FlushCurrentDraftAsync();
        _currentQuestionIndex = index;
        _attempt.CurrentQuestionIndex = index;
        DateTime observedUtc = _clock.Sample().EffectiveUtcNow.UtcDateTime;
        await _databaseService.UpdateAttemptProgressAsync(_attempt.Id, index, observedUtc);
        _attempt.LastActivityUtc = observedUtc;
        ShowQuestion();
    }

    private async void PreviousButton_Clicked(object sender, EventArgs e) =>
        await NavigateToAsync(_currentQuestionIndex - 1);

    private async void NextButton_Clicked(object sender, EventArgs e)
    {
        if (_currentQuestionIndex < _exam.Questions.Count - 1) await NavigateToAsync(_currentQuestionIndex + 1);
        else await ShowReviewAsync();
    }

    private async void ClearAnswerButton_Clicked(object sender, EventArgs e)
    {
        if (_busy) return;
        Question question = _exam.Questions[_currentQuestionIndex];
        _busy = true;
        try
        {
            _autosaveCancellation?.Cancel();
            await _databaseService.ClearAnswerWithOperationAsync(_attempt, question.Id,
                question.ExamQuestionId, _clock.Sample().EffectiveUtcNow.UtcDateTime);
            _selectedOptions.Remove(question.Id);
            _textResponses.Remove(question.Id);
            _documentResponses.Remove(question.Id);
            _drawingResponses.Remove(question.Id);
            _drawingDrawable.Clear();
            _dirtyTextAnswers.Remove(question.Id);
            ShowQuestion();
        }
        catch (Exception ex) { await DisplayAlertAsync("Answer not cleared", ex.Message, "OK"); }
        finally { _busy = false; }
    }

    private async Task ShowReviewAsync()
    {
        await FlushCurrentDraftAsync();
        ExamReviewSummary review = ExamReviewService.Build(_exam, HasAnswer);
        ReviewSummaryLabel.Text = $"Answered {review.AnsweredCount} of {review.Questions.Count} questions.";
        MissingRequiredBanner.IsVisible = !review.CanSubmit;
        MissingRequiredLabel.Text = review.CanSubmit ? string.Empty
            : $"{review.MissingRequiredCount} required question(s) still need an answer.";
        SubmitReviewedButton.IsEnabled = review.CanSubmit;
        SubmitReviewedButton.Opacity = review.CanSubmit ? 1 : 0.55;
        ReviewItemsLayout.Children.Clear();
        foreach (ExamReviewItem item in review.Questions)
        {
            string status = item.IsAnswered ? "Answered"
                : item.IsRequired ? "Required — unanswered" : "Not answered (optional)";
            Color statusColor = item.IsAnswered ? Color.FromArgb("#047857")
                : item.IsRequired ? Color.FromArgb("#B91C1C") : Color.FromArgb("#64748B");
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                ColumnSpacing = 12,
                RowSpacing = 6
            };
            var title = new Label
            {
                Text = $"Question {item.Index + 1}: {item.Prompt}", FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#111827"), LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 2
            };
            var statusLabel = new Label { Text = status, TextColor = statusColor, FontSize = 12 };
            var edit = new Button
            {
                Text = item.IsAnswered ? "Review" : "Answer", BackgroundColor = Color.FromArgb("#E0F2FE"),
                TextColor = Color.FromArgb("#0369A1"), CornerRadius = 8, Padding = new Thickness(14, 6),
                VerticalOptions = LayoutOptions.Center
            };
            int target = item.Index;
            edit.Clicked += async (_, _) => await ReturnToQuestionAsync(target);
            grid.Add(title, 0, 0);
            grid.Add(statusLabel, 0, 1);
            grid.Add(edit, 1, 0);
            Grid.SetRowSpan(edit, 2);
            ReviewItemsLayout.Children.Add(new Border
            {
                Stroke = Color.FromArgb(item.IsRequired && !item.IsAnswered ? "#FCA5A5" : "#E2E8F0"),
                BackgroundColor = Colors.White, Padding = 14,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                Content = grid
            });
        }
        QuestionScrollView.IsVisible = false;
        ReviewScrollView.IsVisible = true;
        await ReviewScrollView.ScrollToAsync(0, 0, false);
    }

    private async Task ReturnToQuestionAsync(int index)
    {
        ReviewScrollView.IsVisible = false;
        QuestionScrollView.IsVisible = true;
        if (index != _currentQuestionIndex) await NavigateToAsync(index);
        else ShowQuestion();
    }

    private async void BackToExamButton_Clicked(object sender, EventArgs e) =>
        await ReturnToQuestionAsync(_currentQuestionIndex);

    private async void SubmitReviewedButton_Clicked(object sender, EventArgs e)
    {
        ExamReviewSummary review = ExamReviewService.Build(_exam, HasAnswer);
        if (!review.CanSubmit)
        {
            await DisplayAlertAsync("Required answers missing",
                "Complete every required question before submitting.", "OK");
            await ShowReviewAsync();
            return;
        }
        bool submit = await DisplayAlertAsync("Submit exam?",
            "This is final. Your answers cannot be changed after submission.",
            "Submit", "Keep reviewing");
        if (submit) await SubmitExamAsync();
    }

    private async Task SubmitExamAsync()
    {
        if (_busy || _attempt.Status != AttemptStatus.InProgress) return;
        await FlushCurrentDraftAsync();
        _busy = true;
        _timer?.Stop();
        NextButton.IsEnabled = PreviousButton.IsEnabled = ClearAnswerButton.IsEnabled = false;
        SubmitReviewedButton.IsEnabled = false;
        _attempt.Status = AttemptStatus.SubmittedLocally;
        _attempt.SubmittedAtUtc = _clock.Sample().EffectiveUtcNow.UtcDateTime;
        var submission = new Submission
        {
            AttemptId = _attempt.Id, CreatedAtUtc = _attempt.SubmittedAtUtc.Value,
            Status = "Pending sync / grading", Score = 0, TotalQuestions = _exam.Questions.Count
        };
        try
        {
            await _databaseService.SubmitAttemptAsync(_attempt, submission);
            try
            {
                await _submissionService.SyncPendingAsync();
                submission.Status = "Submitted for grading";
                await _databaseService.SaveSubmissionAsync(submission);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Submission pending sync: {ex}"); }
            await Navigation.PushAsync(new SubmissionPage(_exam, _attempt, submission));
        }
        catch (Exception ex)
        {
            _attempt.Status = AttemptStatus.InProgress;
            _attempt.SubmittedAtUtc = null;
            _busy = false;
            NextButton.IsEnabled = PreviousButton.IsEnabled = ClearAnswerButton.IsEnabled = true;
            SubmitReviewedButton.IsEnabled = true;
            StartTimer();
            await DisplayAlertAsync("Submission not saved", ex.Message, "OK");
        }
    }
}

public sealed record DrawingPoint(float X, float Y);
public sealed record DrawingStrokeDocument(List<DrawingPoint> Points, string ColorHex, float Thickness);
public sealed record DrawingResponseDocument(List<List<DrawingPoint>> Strokes,
    List<DrawingStrokeDocument>? StyledStrokes = null);
