using Examifo_Desktop.Infrastructure.Persistence;
using Examifo_Desktop.Services;

namespace Examifo_Desktop.Pages;

public sealed class StartupPage : ContentPage
{
    private readonly DatabaseService _databaseService;
    private readonly SessionRestorationService _sessionRestorationService;
    private readonly AuthenticationService _authenticationService;
    private readonly ExamService _examService;
    private readonly AttemptService _attemptService;
    private readonly SubmissionService _submissionService;
    private bool _started;

    public StartupPage(DatabaseService databaseService, SessionRestorationService sessionRestorationService,
        AuthenticationService authenticationService, ExamService examService,
        AttemptService attemptService, SubmissionService submissionService)
    {
        _databaseService = databaseService;
        _sessionRestorationService = sessionRestorationService;
        _authenticationService = authenticationService;
        _examService = examService;
        _attemptService = attemptService;
        _submissionService = submissionService;
        Content = new VerticalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Spacing = 16,
            Children =
            {
                new ActivityIndicator { IsRunning = true, Color = Color.FromArgb("#1479F5") },
                new Label { Text = "Restoring your Examifo session...", TextColor = Color.FromArgb("#475569") }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started) return;
        _started = true;
        Page destination;
        try
        {
            await _databaseService.InitializeAsync();
            SessionRestoreResult restored = await _sessionRestorationService.RestoreAsync();
            destination = restored.Status is SessionRestoreStatus.VerifiedOnline or SessionRestoreStatus.AvailableOffline
                ? new ExamListPage(_databaseService, _examService, _attemptService, _submissionService,
                    _authenticationService)
                : new LoginPage(_databaseService, _authenticationService, _examService,
                    _attemptService, _submissionService);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Startup restoration failed: {ex}");
            destination = new LoginPage(_databaseService, _authenticationService, _examService,
                _attemptService, _submissionService);
        }
        await Navigation.PushAsync(destination);
        Navigation.RemovePage(this);
    }
}
