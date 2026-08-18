using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop;

public partial class App : Application
{
    private readonly DatabaseService _databaseService;
    private readonly Services.AuthenticationService _authenticationService;
    private readonly Services.ExamService _examService;
    private readonly Services.AttemptService _attemptService;
    private readonly Services.SubmissionService _submissionService;
    private readonly Services.ExamAcquisitionCoordinator _examAcquisitionCoordinator;
    private readonly Services.SessionRestorationService _sessionRestorationService;
    private readonly Infrastructure.Sync.ForegroundSyncCoordinator _syncCoordinator;

    public App(
        DatabaseService databaseService,
        Services.AuthenticationService authenticationService,
        Services.ExamService examService,
        Services.AttemptService attemptService,
        Services.SubmissionService submissionService,
        Services.ExamAcquisitionCoordinator examAcquisitionCoordinator,
        Services.SessionRestorationService sessionRestorationService,
        Infrastructure.Sync.ForegroundSyncCoordinator syncCoordinator)
    {
        InitializeComponent();

        _databaseService = databaseService;
        _authenticationService = authenticationService;
        _examService = examService;
        _attemptService = attemptService;
        _submissionService = submissionService;
        _examAcquisitionCoordinator = examAcquisitionCoordinator;
        _sessionRestorationService = sessionRestorationService;
        _syncCoordinator = syncCoordinator;
    }

    protected override Window CreateWindow(
        IActivationState? activationState)
    {
        var window = new Window(
            new NavigationPage(
                    new Pages.StartupPage(_databaseService, _sessionRestorationService,
                    _authenticationService, _examService, _attemptService, _submissionService,
                    _examAcquisitionCoordinator)))
        {
            Width = 820,
            Height = 720,
            MinimumWidth = 420,
            MinimumHeight = 600
        };

#if WINDOWS
        window.Created += (_, _) => CenterWindowsWindow(window);
#endif
        window.Created += (_, _) => _syncCoordinator.Start();
        window.Activated += (_, _) => _syncCoordinator.SetForeground(true);
        window.Resumed += (_, _) => _syncCoordinator.SetForeground(true);
        window.Stopped += (_, _) => _syncCoordinator.SetForeground(false);
        window.Destroying += (_, _) => _syncCoordinator.Dispose();
        return window;
    }

#if WINDOWS
    private static void CenterWindowsWindow(Window window)
    {
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow) return;

        IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        Microsoft.UI.Windowing.AppWindow appWindow =
            Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        Microsoft.UI.Windowing.DisplayArea displayArea =
            Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        Windows.Graphics.RectInt32 workArea = displayArea.WorkArea;
        int width = Math.Min(820, workArea.Width);
        int height = Math.Min(720, workArea.Height);
        int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }
#endif
}
