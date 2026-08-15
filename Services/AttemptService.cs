using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Services;

public sealed class AttemptService(
    SubmissionApiClient apiClient,
    AuthenticationService authenticationService,
    DatabaseService databaseService)
{
    public async Task<Attempt> StartAsync(Exam exam, CancellationToken cancellationToken = default)
    {
        Guid deviceId = await authenticationService.GetDeviceIdAsync();
        long packageVersion = long.Parse(exam.PackageVersion);
        OfflineAuthorizationResponse authorization = await apiClient.AuthorizeAsync(
            exam.Id, new OfflineAuthorizationRequest(deviceId, packageVersion), cancellationToken);

        DateTime now = DateTime.UtcNow;
        if (now < authorization.NotBeforeUtc.UtcDateTime)
            throw new InvalidOperationException($"This exam starts at {authorization.NotBeforeUtc.LocalDateTime:g}.");
        if (now > authorization.MustStartBeforeUtc.UtcDateTime)
            throw new InvalidOperationException("The authorized exam start window has closed.");

        DateTime durationDeadline = authorization.DurationSeconds.HasValue
            ? now.AddSeconds(authorization.DurationSeconds.Value)
            : authorization.MustSubmitBeforeUtc.UtcDateTime;

        var attempt = new Attempt
        {
            Id = authorization.AttemptId,
            ExamId = exam.Id,
            AuthorizationId = authorization.AuthorizationId,
            DeviceId = authorization.DeviceId,
            PackageVersion = authorization.PackageVersion,
            Status = AttemptStatus.InProgress,
            StartedAtUtc = now,
            DeadlineUtc = durationDeadline < authorization.MustSubmitBeforeUtc.UtcDateTime
                ? durationDeadline
                : authorization.MustSubmitBeforeUtc.UtcDateTime,
            NextSequence = 1
        };

        await databaseService.StartAuthorizedAttemptAsync(attempt, authorization.AuthorizationToken);
        return attempt;
    }
}
