using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Services;

public sealed class AttemptService(
    SubmissionApiClient apiClient,
    AuthenticationService authenticationService,
    DatabaseService databaseService,
    OfflineAuthorizationStore authorizationStore,
    TrustedServerTimeService trustedTime)
{
    public AttemptClock CreateClock(Attempt attempt, TimeProvider? timeProvider = null) =>
        new(attempt.DeadlineUtc, attempt.LastActivityUtc, trustedTime, timeProvider);

    public async Task<Attempt?> GetResumableAttemptAsync(Exam exam,
        CancellationToken cancellationToken = default)
    {
        AuthSession session = await authenticationService.GetCurrentSessionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Sign in again before continuing this exam.");
        if (session.UserId is not { } candidateId || candidateId == Guid.Empty)
            throw new InvalidOperationException("The signed-in candidate identity is unavailable.");
        Attempt? attempt = await databaseService.GetInProgressAttemptForExamAsync(
            exam.Id, candidateId, session.DeviceId, cancellationToken);
        if (attempt is null) return null;
        if (!long.TryParse(exam.PackageVersion, out long packageVersion)
            || attempt.PackageVersion != packageVersion)
        {
            // Never apply answers from one immutable package to another. For a multi-attempt
            // exam, leave the old attempt untouched and let the authorization endpoint decide
            // whether the candidate may start a fresh attempt against the current package.
            if (exam.MaxAttempts > 1) return null;
            throw new InvalidOperationException(
                "The saved attempt uses a different exam package. Reconnect before continuing.");
        }
        return attempt;
    }

    public async Task<string> ExplainOfflineAccessFailureAsync(Exam exam, Exception exception,
        CancellationToken cancellationToken = default)
    {
        if (exception is AuthApiException apiException)
            return apiException.Message;
        AuthSession? session = await authenticationService.GetCurrentSessionAsync(cancellationToken);
        Attempt? latest = await databaseService.GetLatestAttemptForExamAsync(
            exam.Id, session?.UserId ?? Guid.Empty, session?.DeviceId ?? Guid.Empty, cancellationToken);
        if (latest?.Status is AttemptStatus.SubmittedLocally or AttemptStatus.Syncing
            or AttemptStatus.Synced && exam.MaxAttempts <= 1)
            return "You have already completed or submitted this exam attempt. "
                + "Reconnect to synchronize or view its final status.";
        if (latest?.Status is AttemptStatus.Rejected or AttemptStatus.NeedsReview)
            return "Your previous attempt cannot be started again. "
                + "Reconnect to review its status with Examifo.";
        if (exam.ExistingAttemptStatus is "submitted" or "submitted_locally" or "syncing"
            or "synced" or "completed" or "graded" && exam.MaxAttempts <= 1)
            return "You have already taken or submitted this exam. "
                + "Reconnect to view its current result or synchronization status.";
        if (exception is HttpRequestException or TaskCanceledException)
            return "The exam content is available offline, but this device has no unused offline "
                + "start authorization. Connect to the internet and press Continue once before going offline.";
        if (exception is InvalidOperationException
            && exception.Message.Contains("not been authorized", StringComparison.OrdinalIgnoreCase))
            return "This exam has no unused start authorization. Return to Exam Details and press "
                + "Continue while connected, or resume an attempt that is already in progress.";
        if (exam.MaxAttempts > 1 && latest?.Status is AttemptStatus.SubmittedLocally
            or AttemptStatus.Syncing or AttemptStatus.Synced)
            return "Your previous attempt is complete, but Examifo did not issue the next attempt authorization. "
                + exception.Message;
        return exception.Message;
    }

    public async Task<StoredOfflineAuthorization> GetOrCreateAuthorizationAsync(
        Exam exam, CancellationToken cancellationToken = default)
    {
        AuthSession session = await authenticationService.GetCurrentSessionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Sign in again before requesting offline access.");
        if (session.UserId is not { } candidateId || candidateId == Guid.Empty)
            throw new InvalidOperationException("The signed-in candidate identity is unavailable.");
        Guid deviceId = session.DeviceId;
        if (!long.TryParse(exam.PackageVersion, out long packageVersion) || packageVersion <= 0)
            throw new InvalidOperationException("Download a valid exam package before requesting offline access.");

        StoredOfflineAuthorization? stored = await authorizationStore.FindForExamAsync(
            exam.Id, candidateId, cancellationToken);
        if (stored is not null)
        {
            Attempt? authorizedAttempt = await databaseService.GetAttemptAsync(stored.AttemptId);
            if (authorizedAttempt?.Status is AttemptStatus.SubmittedLocally or AttemptStatus.Syncing
                or AttemptStatus.Synced or AttemptStatus.Rejected or AttemptStatus.NeedsReview)
            {
                await authorizationStore.RemoveAsync(stored.AuthorizationId, cancellationToken);
                await databaseService.RemoveAttemptAuthorizationAsync(stored.AuthorizationId, cancellationToken);
                stored = null;
            }
        }
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
        await databaseService.SaveAttemptAuthorizationAsync(new AttemptAuthorizationRecord
        {
            AuthorizationId = authorization.AuthorizationId,
            AttemptId = authorization.AttemptId,
            ExamId = authorization.ExamId,
            CandidateId = authorization.CandidateId,
            DeviceId = authorization.DeviceId,
            PackageVersion = authorization.PackageVersion,
            NotBeforeUtc = authorization.NotBeforeUtc.UtcDateTime,
            MustStartBeforeUtc = authorization.MustStartBeforeUtc.UtcDateTime,
            MustSubmitBeforeUtc = authorization.MustSubmitBeforeUtc.UtcDateTime,
            DurationSeconds = authorization.DurationSeconds,
            AttemptNumber = authorization.AttemptNumber,
            ServerTimeUtc = authorization.ServerTimeUtc.UtcDateTime,
            State = "Authorized"
        }, authorization.ShuffleSeed, authorization.AuthorizationToken, cancellationToken);
        return result;
    }

    public async Task<Attempt> StartAuthorizedAsync(Exam exam, CancellationToken cancellationToken = default)
    {
        AuthSession session = await authenticationService.GetCurrentSessionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Sign in again before starting this exam.");
        if (session.UserId is not { } candidateId || candidateId == Guid.Empty)
            throw new InvalidOperationException("The signed-in candidate identity is unavailable.");
        StoredOfflineAuthorization authorization = await authorizationStore.FindForExamAsync(
            exam.Id, candidateId, cancellationToken)
            ?? throw new InvalidOperationException("This exam has not been authorized for offline start.");
        Guid currentDeviceId = session.DeviceId;
        if (authorization.DeviceId != currentDeviceId)
            throw new InvalidOperationException("This offline authorization belongs to a different device.");
        if (authorization.CandidateId != candidateId)
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
            CandidateId = authorization.CandidateId,
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

        attempt.ShuffleSeed = authorization.ShuffleSeed;
        await databaseService.StartAuthorizedAttemptAsync(
            attempt, authorization.AuthorizationToken, authorization.ShuffleSeed, cancellationToken);
        await authorizationStore.RemoveAsync(authorization.AuthorizationId, cancellationToken);
        await databaseService.RemoveAttemptAuthorizationAsync(authorization.AuthorizationId, cancellationToken);
        return attempt;
    }

    public async Task<Attempt> StartAsync(Exam exam, CancellationToken cancellationToken = default)
    {
        await GetOrCreateAuthorizationAsync(exam, cancellationToken);
        return await StartAuthorizedAsync(exam, cancellationToken);
    }

    public async Task CancelAuthorizationAsync(Guid examId, CancellationToken cancellationToken = default)
    {
        AuthSession session = await authenticationService.GetCurrentSessionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Sign in again before cancelling offline access.");
        if (session.UserId is not { } candidateId || candidateId == Guid.Empty)
            throw new InvalidOperationException("The signed-in candidate identity is unavailable.");
        StoredOfflineAuthorization? authorization = await authorizationStore.FindForExamAsync(
            examId, candidateId, cancellationToken);
        if (authorization is null) return;
        await apiClient.CancelAuthorizationAsync(authorization.AuthorizationId, cancellationToken);
        await authorizationStore.RemoveAsync(authorization.AuthorizationId, cancellationToken);
        await databaseService.RemoveAttemptAuthorizationAsync(authorization.AuthorizationId, cancellationToken);
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
