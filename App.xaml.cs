using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop;

public partial class App : Application
{
    private readonly DatabaseService _databaseService;
    private readonly Services.AuthenticationService _authenticationService;
    private readonly Services.ExamService _examService;
    private readonly Services.AttemptService _attemptService;
    private readonly Services.SubmissionService _submissionService;
    private readonly Services.SessionRestorationService _sessionRestorationService;

    public App(
        DatabaseService databaseService,
        Services.AuthenticationService authenticationService,
        Services.ExamService examService,
        Services.AttemptService attemptService,
        Services.SubmissionService submissionService,
        Services.SessionRestorationService sessionRestorationService)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _authenticationService = authenticationService;
        _examService = examService;
        _attemptService = attemptService;
        _submissionService = submissionService;
        _sessionRestorationService = sessionRestorationService;
    }

    protected override Window CreateWindow(
        IActivationState? activationState)
    {
        return new Window(
            new NavigationPage(
                new Pages.StartupPage(_databaseService, _sessionRestorationService,
                    _authenticationService, _examService, _attemptService, _submissionService)));
    }
}
