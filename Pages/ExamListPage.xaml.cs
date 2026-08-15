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
    private readonly AuthenticationService _authenticationService;
    private bool _loaded;

    public ExamListPage(DatabaseService databaseService, ExamService examService,
        AttemptService attemptService, SubmissionService submissionService,
        AuthenticationService authenticationService)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _examService = examService;
        _attemptService = attemptService;
        _submissionService = submissionService;
        _authenticationService = authenticationService;

        ExamCollectionView.SelectionChanged +=
            ExamCollectionView_SelectionChanged;

        ExamCollectionView.ItemsSource = _exams;
    }

    private async void LogoutButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            await _authenticationService.LogoutAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Server logout failed: {ex}");
            bool localOnly = await DisplayAlertAsync("Server unavailable",
                "Examifo could not revoke the server session. Sign out on this device only?", "Sign out locally", "Cancel");
            if (!localOnly) return;
            await _authenticationService.LogoutAsync(localOnly: true);
        }

        var login = new LoginPage(_databaseService, _authenticationService, _examService,
            _attemptService, _submissionService);
        await Navigation.PushAsync(login);
        foreach (Page page in Navigation.NavigationStack.Where(page => page != login).ToList())
            Navigation.RemovePage(page);
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
            _exams = new List<Exam>();
            await DisplayAlertAsync("Unable to load exams",
                "Your assigned exams could not be retrieved. Please try again.", "OK");
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

}
