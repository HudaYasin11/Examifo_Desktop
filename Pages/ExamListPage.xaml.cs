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
    private readonly ExamAcquisitionCoordinator _examAcquisitionCoordinator;
    private readonly SemaphoreSlim _toastGate = new(1, 1);
    private bool _loaded;

    public ExamListPage(DatabaseService databaseService, ExamService examService,
        AttemptService attemptService, SubmissionService submissionService,
        AuthenticationService authenticationService,
        ExamAcquisitionCoordinator examAcquisitionCoordinator)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _examService = examService;
        _attemptService = attemptService;
        _submissionService = submissionService;
        _authenticationService = authenticationService;
        _examAcquisitionCoordinator = examAcquisitionCoordinator;

        ExamCollectionView.SelectionChanged +=
            ExamCollectionView_SelectionChanged;

        ExamCollectionView.ItemsSource = _exams;
    }

    private void ApplyAcquisitionUpdate(ExamAcquisitionUpdate update)
    {
        (update.Exam.OfflineStatus, update.Exam.OfflineStatusColor) = update.State switch
        {
            ExamAcquisitionState.Checking => ("Checking offline availability…", "#64748B"),
            ExamAcquisitionState.Downloading => ("Downloading for offline use…", "#1479F5"),
            ExamAcquisitionState.OfflineReady => ("✓ Available offline", "#047857"),
            ExamAcquisitionState.Unavailable => ("Online access only", "#92400E"),
            _ => ("Offline download unavailable — retry when online", "#B91C1C")
        };
        if (update.NewlyAvailable)
            _ = ShowOfflineToastAsync($"✓ {update.Exam.Title} is now available offline");
        if (update.State == ExamAcquisitionState.Failed && update.Detail is not null)
            System.Diagnostics.Debug.WriteLine($"Automatic exam download failed: {update.Detail}");
    }

    private async Task ShowOfflineToastAsync(string message)
    {
        await _toastGate.WaitAsync();
        try
        {
            OfflineToastLabel.Text = message;
            OfflineToast.Opacity = 0;
            OfflineToast.IsVisible = true;
            await OfflineToast.FadeToAsync(1, 180);
            await Task.Delay(2200);
            await OfflineToast.FadeToAsync(0, 250);
            OfflineToast.IsVisible = false;
        }
        finally { _toastGate.Release(); }
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
            _attemptService, _submissionService, _examAcquisitionCoordinator);
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Real exam loading failed: {ex}");
            _exams = new List<Exam>();
            await DisplayAlertAsync("Unable to load exams",
                "Your assigned exams could not be retrieved. Please try again.", "OK");
        }

        ExamCollectionView.ItemsSource = _exams;
        var progress = new Progress<ExamAcquisitionUpdate>(ApplyAcquisitionUpdate);
        Task acquisition = _examAcquisitionCoordinator.AcquireAvailablePackagesAsync(_exams, progress);
        Task synchronization = SyncPendingSafelyAsync();
        await Task.WhenAll(acquisition, synchronization);
    }

    private async void RefreshView_Refreshing(object sender, EventArgs e)
    {
        try
        {
            _exams = await _examService.GetAvailableExamsAsync();
            ExamCollectionView.ItemsSource = _exams;
            var progress = new Progress<ExamAcquisitionUpdate>(ApplyAcquisitionUpdate);
            await _examAcquisitionCoordinator.AcquireAvailablePackagesAsync(_exams, progress);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Exam refresh failed: {ex}");
            await DisplayAlertAsync("Unable to refresh exams",
                "The latest assigned exams could not be retrieved. Your saved offline list is unchanged.", "OK");
        }
        finally
        {
            ExamRefreshView.IsRefreshing = false;
        }
    }

    private async Task SyncPendingSafelyAsync()
    {
        try { await _submissionService.SyncPendingAsync(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pending sync will retry later: {ex}");
        }
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
