using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Services;

public sealed class AttemptService(
    SubmissionApiClient apiClient,
    AuthenticationService authenticationService,
    DatabaseService databaseService,
    OfflineAuthorizationStore authorizationStore,
    TrustedServerTimeService trustedTime)
{
    public async Task<StoredOfflineAuthorization> GetOrCreateAuthorizationAsync(
        Exam exam, CancellationToken cancellationToken = default)
    {
        Guid deviceId = await authenticationService.GetDeviceIdAsync();
        if (!long.TryParse(exam.PackageVersion, out long packageVersion) || packageVersion <= 0)
            throw new InvalidOperationException("Download a valid exam package before requesting offline access.");

        StoredOfflineAuthorization? stored = await authorizationStore.FindForExamAsync(exam.Id, cancellationToken);
        if (stored is not null && stored.DeviceId == deviceId && stored.PackageVersion == packageVersion
            && trustedTime.UtcNow <= stored.MustStartBeforeUtc)
            return stored;
        if (stored is not null)
            await authorizationStore.RemoveAsync(stored.AuthorizationId, cancellationToken);

        DateTimeOffset requestStarted = DateTimeOffset.UtcNow;
        OfflineAuthorizationResponse authorization = await apiClient.AuthorizeAsync(
            exam.Id, new OfflineAuthorizationRequest(deviceId, packageVersion), cancellationToken);
        trustedTime.RecordSample(authorization.ServerTimeUtc, requestStarted, DateTimeOffset.UtcNow);
        ValidateAuthorization(authorization, exam.Id, deviceId, packageVersion);
        StoredOfflineAuthorization result = StoredOfflineAuthorization.FromResponse(authorization);
        await authorizationStore.SaveAsync(result, cancellationToken);
        return result;
    }

    public async Task<Attempt> StartAuthorizedAsync(Exam exam, CancellationToken cancellationToken = default)
    {
        StoredOfflineAuthorization authorization = await authorizationStore.FindForExamAsync(exam.Id, cancellationToken)
            ?? throw new InvalidOperationException("This exam has not been authorized for offline start.");
        Guid currentDeviceId = await authenticationService.GetDeviceIdAsync();
        if (authorization.DeviceId != currentDeviceId)
            throw new InvalidOperationException("This offline authorization belongs to a different device.");
        AuthSession? session = await authenticationService.GetCurrentSessionAsync(cancellationToken);
        if (session?.UserId is { } userId && authorization.CandidateId != userId)
            throw new InvalidOperationException("This offline authorization belongs to a different candidate.");
        if (!long.TryParse(exam.PackageVersion, out long packageVersion)
            || packageVersion != authorization.PackageVersion)
            throw new InvalidOperationException("The authorized exam package has changed. Re-authorize while online.");

        DateTimeOffset now = trustedTime.UtcNow;
        if (now < authorization.NotBeforeUtc)
            throw new InvalidOperationException($"This exam starts at {authorization.NotBeforeUtc.LocalDateTime:g}.");
        if (now > authorization.MustStartBeforeUtc)
            throw new InvalidOperationException("The authorized exam start window has closed.");

        DateTime durationDeadline = trustedTime.CalculateDeadline(
            now, authorization.DurationSeconds, authorization.MustSubmitBeforeUtc).UtcDateTime;

        var attempt = new Attempt
        {
            Id = authorization.AttemptId,
            ExamId = exam.Id,
            AuthorizationId = authorization.AuthorizationId,
            DeviceId = authorization.DeviceId,
            PackageVersion = authorization.PackageVersion,
            Status = AttemptStatus.InProgress,
            StartedAtUtc = now.UtcDateTime,
            DeadlineUtc = durationDeadline < authorization.MustSubmitBeforeUtc.UtcDateTime
                ? durationDeadline
                : authorization.MustSubmitBeforeUtc.UtcDateTime,
            NextSequence = 1
        };

        await databaseService.StartAuthorizedAttemptAsync(attempt, authorization.AuthorizationToken);
        await authorizationStore.RemoveAsync(authorization.AuthorizationId, cancellationToken);
        return attempt;
    }

    public async Task<Attempt> StartAsync(Exam exam, CancellationToken cancellationToken = default)
    {
        await GetOrCreateAuthorizationAsync(exam, cancellationToken);
        return await StartAuthorizedAsync(exam, cancellationToken);
    }

    public async Task CancelAuthorizationAsync(Guid examId, CancellationToken cancellationToken = default)
    {
        StoredOfflineAuthorization? authorization = await authorizationStore.FindForExamAsync(examId, cancellationToken);
        if (authorization is null) return;
        await apiClient.CancelAuthorizationAsync(authorization.AuthorizationId, cancellationToken);
        await authorizationStore.RemoveAsync(authorization.AuthorizationId, cancellationToken);
    }

    public async Task<IReadOnlyList<OfflineAuthorizationSummary>> GetServerAuthorizationsAsync(
        CancellationToken cancellationToken = default) =>
        await apiClient.GetAuthorizationsAsync(await authenticationService.GetDeviceIdAsync(), cancellationToken);

    private static void ValidateAuthorization(OfflineAuthorizationResponse value, Guid examId,
        Guid deviceId, long packageVersion)
    {
        if (value.AuthorizationId == Guid.Empty || value.AttemptId == Guid.Empty
            || value.ExamId != examId || value.CandidateId == Guid.Empty || value.DeviceId != deviceId
            || value.PackageVersion != packageVersion || value.NotBeforeUtc == default
            || value.MustStartBeforeUtc < value.NotBeforeUtc || value.MustSubmitBeforeUtc < value.NotBeforeUtc
            || value.DurationSeconds is <= 0 || value.AttemptNumber <= 0
            || string.IsNullOrWhiteSpace(value.ShuffleSeed) || value.ServerTimeUtc == default
            || string.IsNullOrWhiteSpace(value.AuthorizationToken))
            throw new InvalidDataException("Examifo returned an invalid offline authorization.");
    }
}
