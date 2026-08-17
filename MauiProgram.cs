using Examifo_Desktop.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Security;
using Examifo_Desktop.Infrastructure.Sync;
using Examifo_Desktop.Services;

namespace Examifo_Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<EncryptionService>();
        builder.Services.AddSingleton<ILocalDatabasePathProvider, MauiLocalDatabasePathProvider>();
        builder.Services.AddSingleton<ILocalPackagePathProvider, MauiLocalPackagePathProvider>();
        builder.Services.AddSingleton<DatabaseService>();
        builder.Services.AddSingleton<OutboxService>();
        builder.Services.AddSingleton(new HttpClient
        {
            BaseAddress = new Uri("https://examifo.com/"),
            Timeout = TimeSpan.FromMinutes(5)
        });
        builder.Services.AddSingleton<AuthApiClient>();
        builder.Services.AddSingleton<IInstallationIdentityStore, MauiPreferencesInstallationIdentityStore>();
        builder.Services.AddSingleton<InstallationIdentityService>();
        builder.Services.AddSingleton<ISecureValueStore, MauiSecureValueStore>();
        builder.Services.AddSingleton<AuthSessionStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ITrustedTimeStore, MauiPreferencesTrustedTimeStore>();
        builder.Services.AddSingleton<TrustedServerTimeService>();
        builder.Services.AddSingleton<SessionStateService>();
        builder.Services.AddSingleton<ITokenRefreshClient>(serviceProvider =>
            serviceProvider.GetRequiredService<AuthApiClient>());
        builder.Services.AddSingleton<TokenRefreshCoordinator>();
        builder.Services.AddSingleton<IAuthenticatedTokenProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<TokenRefreshCoordinator>());
        builder.Services.AddSingleton<AuthenticatedHttpClient>();
        builder.Services.AddSingleton<IdentityApiClient>();
        builder.Services.AddSingleton<ICurrentIdentityClient>(serviceProvider =>
            serviceProvider.GetRequiredService<IdentityApiClient>());
        builder.Services.AddSingleton<SessionRestorationService>();
        builder.Services.AddSingleton<SessionLogoutService>();
        builder.Services.AddSingleton<OfflineAuthorizationStore>();
        builder.Services.AddSingleton<AuthenticationService>();
        builder.Services.AddSingleton<ExamApiClient>();
        builder.Services.AddSingleton<DeviceApiClient>();
        builder.Services.AddSingleton<DeviceService>();
        builder.Services.AddSingleton<SubmissionApiClient>();
        builder.Services.AddSingleton<ExamService>();
        builder.Services.AddSingleton<ExamPackageStore>();
        builder.Services.AddSingleton<ExamAcquisitionCoordinator>();
        builder.Services.AddSingleton<AttemptService>();
        builder.Services.AddSingleton<SubmissionService>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
