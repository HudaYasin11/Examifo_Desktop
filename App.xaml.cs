using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop;

public partial class App : Application
{
    private readonly DatabaseService _databaseService;

    public App(DatabaseService databaseService)
    {
        InitializeComponent();

        _databaseService = databaseService;
    }

    protected override Window CreateWindow(
        IActivationState? activationState)
    {
        _ = InitializeDatabaseAsync();

        return new Window(
            new NavigationPage(
                new Pages.LoginPage(_databaseService)));
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