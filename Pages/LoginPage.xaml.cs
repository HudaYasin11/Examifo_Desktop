using Examifo_Desktop.Infrastructure.Persistence;
using Examifo_Desktop.Services;

namespace Examifo_Desktop.Pages;

public partial class LoginPage : ContentPage
{
    private readonly AuthenticationService _authenticationService;
    private readonly DatabaseService _databaseService;
    private readonly ExamService _examService;
    private readonly AttemptService _attemptService;
    private readonly SubmissionService _submissionService;
    private readonly ExamAcquisitionCoordinator _examAcquisitionCoordinator;
    private CancellationTokenSource? _loginCancellation;
    private bool _loginInProgress;

    public LoginPage(
        DatabaseService databaseService,
        AuthenticationService authenticationService,
        ExamService examService,
        AttemptService attemptService,
        SubmissionService submissionService,
        ExamAcquisitionCoordinator examAcquisitionCoordinator)
    {
        InitializeComponent();

        _authenticationService = authenticationService;
        _databaseService = databaseService;
        _examService = examService;
        _attemptService = attemptService;
        _submissionService = submissionService;
        _examAcquisitionCoordinator = examAcquisitionCoordinator;
    }

    private async void LoginButton_Clicked(object sender, EventArgs e)
    {
        if (_loginInProgress) return;
        _loginInProgress = true;
        _loginCancellation = new CancellationTokenSource();
        ErrorLabel.IsVisible = false;
        LoginButton.IsEnabled = false;
        EmailEntry.IsEnabled = false;
        PasswordEntry.IsEnabled = false;
        LoginButton.Text = "Signing in...";

        string email = EmailEntry.Text ?? string.Empty;
        string password = PasswordEntry.Text ?? string.Empty;

        try
        {
            LoginResult result = await _authenticationService.LoginAsync(email, password, _loginCancellation.Token);
            if (result.Success)
            {
                await Navigation.PushAsync(new ExamListPage(
                    _databaseService, _examService, _attemptService, _submissionService,
                    _authenticationService, _examAcquisitionCoordinator));
                Navigation.RemovePage(this);
                return;
            }

            ErrorLabel.Text = result.ErrorMessage;
            ErrorLabel.IsVisible = true;
            if (result.FailureKind == LoginFailureKind.InvalidCredentials)
            {
                PasswordEntry.Text = string.Empty;
                PasswordEntry.Focus();
            }
        }
        catch (OperationCanceledException)
        {
            // Navigating away deliberately cancels an in-flight login request.
        }
        finally
        {
            _loginCancellation?.Dispose();
            _loginCancellation = null;
            _loginInProgress = false;
            LoginButton.IsEnabled = true;
            EmailEntry.IsEnabled = true;
            PasswordEntry.IsEnabled = true;
            LoginButton.Text = "Login";
        }
    }

    protected override void OnDisappearing()
    {
        _loginCancellation?.Cancel();
        base.OnDisappearing();
    }
}
