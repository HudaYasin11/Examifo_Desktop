using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop;

public partial class App : Application
{
    private readonly DatabaseService _databaseService;
    private readonly Services.AuthenticationService _authenticationService;
    private readonly Services.ExamService _examService;
    private readonly Services.AttemptService _attemptService;
    private readonly Services.SubmissionService _submissionService;

    public App(
        DatabaseService databaseService,
        Services.AuthenticationService authenticationService,
        Services.ExamService examService,
        Services.AttemptService attemptService,
        Services.SubmissionService submissionService)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _authenticationService = authenticationService;
        _examService = examService;
        _attemptService = attemptService;
        _submissionService = submissionService;
    }

    protected override Window CreateWindow(
        IActivationState? activationState)
    {
        _ = InitializeDatabaseAsync();

        return new Window(
            new NavigationPage(
                new Pages.LoginPage(_databaseService, _authenticationService, _examService,
                    _attemptService, _submissionService)));
    }

    private async Task InitializeDatabaseAsync()
    {
        try
        {
            await _databaseService.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Database initialization failed: {ex}");
        }
    }
}
