using Examifo_Desktop.Services;

namespace Examifo_Desktop.Pages;

public partial class LoginPage : ContentPage
{
    private readonly AuthenticationService _authenticationService;

    public LoginPage()
    {
        InitializeComponent();

        _authenticationService = new AuthenticationService();
    }

    private async void LoginButton_Clicked(object sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;

        string email = EmailEntry.Text ?? string.Empty;
        string password = PasswordEntry.Text ?? string.Empty;

        bool success = await _authenticationService.LoginAsync(email, password);

        if (success)
        {
            await Navigation.PushAsync(new ExamListPage());
        }
        else
        {
            ErrorLabel.Text = "Invalid email or password.";
            ErrorLabel.IsVisible = true;
        }
    }
}