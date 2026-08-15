using Examifo_Desktop.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

using Examifo_Desktop.Infrastructure.Api.Clients;
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

        builder.Services.AddSingleton<DatabaseService>();
        builder.Services.AddSingleton(new HttpClient
        {
            BaseAddress = new Uri("https://examifo.com/"),
            Timeout = TimeSpan.FromMinutes(5)
        });
        builder.Services.AddSingleton<AuthApiClient>();
        builder.Services.AddSingleton<AuthenticationService>();
        builder.Services.AddSingleton<ExamApiClient>();
        builder.Services.AddSingleton<SubmissionApiClient>();
        builder.Services.AddSingleton<ExamService>();
        builder.Services.AddSingleton<AttemptService>();
        builder.Services.AddSingleton<SubmissionService>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
