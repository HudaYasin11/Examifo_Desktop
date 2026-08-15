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

    public LoginPage(
        DatabaseService databaseService,
        AuthenticationService authenticationService,
        ExamService examService,
        AttemptService attemptService,
        SubmissionService submissionService)
    {
        InitializeComponent();

        _authenticationService = authenticationService;
        _databaseService = databaseService;
        _examService = examService;
        _attemptService = attemptService;
        _submissionService = submissionService;
    }

    private async void LoginButton_Clicked(object sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;
        LoginButton.IsEnabled = false;
        LoginButton.Text = "Signing in...";

        string email = EmailEntry.Text ?? string.Empty;
        string password = PasswordEntry.Text ?? string.Empty;

        try
        {
            LoginResult result = await _authenticationService.LoginAsync(email, password);
            if (result.Success)
            {
                await Navigation.PushAsync(new ExamListPage(
                    _databaseService, _examService, _attemptService, _submissionService));
                return;
            }

            ErrorLabel.Text = result.ErrorMessage;
            ErrorLabel.IsVisible = true;
        }
        finally
        {
            LoginButton.IsEnabled = true;
            LoginButton.Text = "Login";
        }
    }
}
