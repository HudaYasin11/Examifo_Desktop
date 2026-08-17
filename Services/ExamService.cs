using System.Security.Cryptography;
using System.Collections.Concurrent;
using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Infrastructure.Persistence;

namespace Examifo_Desktop.Services;

public sealed class ExamService(ExamApiClient examApiClient, TrustedServerTimeService trustedTime,
    DatabaseService databaseService, ILocalPackagePathProvider packagePaths,
    ExamPackageStore packageStore, SessionStateService sessionState)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _packageGates = new();

    public async Task<List<Exam>> GetAvailableExamsAsync(
        DateTimeOffset? modifiedSinceUtc = null,
        CancellationToken cancellationToken = default)
    {
        Guid candidateId = sessionState.Current.UserId
            ?? throw new InvalidOperationException("The signed-in candidate identity is unavailable.");
        List<AvailableExamRecord> cached = await databaseService.GetCachedExamCatalogueAsync(
            candidateId, cancellationToken);
        try
        {
            DateTimeOffset requestStarted = DateTimeOffset.UtcNow;
            // Fetch the authoritative full catalogue. modifiedSinceUtc is optional in the
            // contract; full replacement also removes exams no longer assigned/eligible.
            AvailableExamsResponse catalogue = await examApiClient.GetAvailableExamsAsync(
                modifiedSinceUtc: null, cancellationToken);
            trustedTime.RecordSample(catalogue.ServerTimeUtc, requestStarted, DateTimeOffset.UtcNow);
            List<Exam> changes = AvailableExamMapper.Map(catalogue);
            await databaseService.ReplaceExamCatalogueAsync(candidateId, changes.Select(ToRecord),
                catalogue.ServerTimeUtc.UtcDateTime, fullRefresh: true, cancellationToken);
            return (await databaseService.GetCachedExamCatalogueAsync(candidateId, cancellationToken))
                .Select(FromRecord).ToList();
        }
        catch when (cached.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            return cached.Select(FromRecord).ToList();
        }
    }

    public async Task<Exam> PrepareExamAsync(Exam summary, CancellationToken cancellationToken = default)
    {
        SemaphoreSlim gate = _packageGates.GetOrAdd(summary.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await PrepareExamCoreAsync(summary, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task<Exam> PrepareExamCoreAsync(Exam summary, CancellationToken cancellationToken)
    {
        ExamMetadataResponse? metadata = await TryRefreshMetadataAsync(summary, cancellationToken);
        Exam? installed = await TryLoadInstalledExamAsync(summary, cancellationToken);
        if (installed is not null) return installed;
        if (!summary.CanDownload && summary.Questions.Count == 0)
            throw new InvalidOperationException("This assigned exam is not currently available for download.");
        metadata ??= await examApiClient.GetExamAsync(summary.Id, cancellationToken);
        ValidateMetadata(metadata, summary.Id);
        PackageManifestResponse manifest = await examApiClient.GetManifestAsync(summary.Id, cancellationToken);
        Version currentVersion = Version.TryParse(AppInfo.Current.VersionString, out Version? parsedVersion)
            ? parsedVersion : new Version(1, 0);
        if (!long.TryParse(summary.PackageVersion, out long expectedVersion))
            throw new InvalidDataException("The assigned exam has an invalid package version.");
        ExamPackageValidator.ValidateManifest(manifest, summary.Id, expectedVersion,
            summary.PackageHash, summary.PackageSizeBytes, currentVersion);

        DownloadedExamRecord? previous = await databaseService.GetDownloadedExamAsync(
            summary.Id, cancellationToken);
        string? previousContentHash = previous is not null
            && string.Equals(previous.State, "Ready", StringComparison.OrdinalIgnoreCase)
                ? previous.ContentHash : null;

        Directory.CreateDirectory(packagePaths.TemporaryPackageDirectory);
        string temporaryPath = Path.Combine(packagePaths.TemporaryPackageDirectory,
            $"{summary.Id:N}-{manifest.Version}-{Guid.NewGuid():N}.download");
        ExamPackageV1 package;
        byte[] packageBytes;
        try
        {
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                PackageDownloadResult result = await examApiClient.DownloadPackageAsync(
                    manifest.DownloadUrl, destination, manifest.SizeBytes,
                    previousContentHash, cancellationToken);
                if (result == PackageDownloadResult.NotModified)
                {
                    Exam? current = await TryLoadInstalledExamAsync(summary, cancellationToken);
                    if (current is not null) return current;
                    throw new InvalidDataException(
                        "The server reported an unchanged package, but no valid matching local package exists.");
                }
                await destination.FlushAsync(cancellationToken);
            }

            await using (var packageFile = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(
                    packageFile, cancellationToken)).ToLowerInvariant();
                if (!string.Equals(actualHash, manifest.ContentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Exam package integrity verification failed.");
                packageFile.Position = 0;
                package = ExamPackageValidator.ParseCandidateSafePackage(packageFile, summary.Id);
            }

            packageBytes = await File.ReadAllBytesAsync(temporaryPath, cancellationToken);
            PackageInstallation installation = await packageStore.InstallAsync(summary.Id,
                manifest.Version, manifest.ContentHash, packageBytes, cancellationToken);
            try
            {
                await databaseService.SaveDownloadedExamAsync(new DownloadedExamRecord
                {
                    ExamId = summary.Id,
                    PackageVersion = manifest.Version,
                    ContentHash = manifest.ContentHash.ToLowerInvariant(),
                    LocalPath = installation.LocalPath,
                    DownloadedAtUtc = DateTime.UtcNow,
                    State = "Ready"
                }, cancellationToken);
            }
            catch
            {
                if (installation.Created) packageStore.DeleteIfManaged(installation.LocalPath);
                throw;
            }
            if (previous is not null
                && !string.Equals(previous.LocalPath, installation.LocalPath, StringComparison.OrdinalIgnoreCase))
                packageStore.DeleteIfManaged(previous.LocalPath);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException ex) { System.Diagnostics.Debug.WriteLine($"Temporary package cleanup deferred: {ex}"); }
        }


        return BuildExam(summary, package, metadata.Title, metadata.Description,
            metadata.DurationMinutes, metadata.StartsAtUtc, metadata.EndsAtUtc,
            metadata.MaxAttempts, metadata.ProctoringEnabled, manifest.Version, manifest.ContentHash);
    }

    private async Task<ExamMetadataResponse?> TryRefreshMetadataAsync(Exam summary,
        CancellationToken cancellationToken)
    {
        if (Microsoft.Maui.Networking.Connectivity.Current.NetworkAccess
            != Microsoft.Maui.Networking.NetworkAccess.Internet)
            return null;
        try
        {
            ExamMetadataResponse metadata = await examApiClient.GetExamAsync(summary.Id, cancellationToken);
            ValidateMetadata(metadata, summary.Id);
            summary.Title = metadata.Title;
            summary.Description = metadata.Description ?? string.Empty;
            summary.DurationMinutes = metadata.DurationMinutes ?? summary.DurationMinutes;
            summary.StartsAtUtc = metadata.StartsAtUtc?.UtcDateTime ?? summary.StartsAtUtc;
            summary.EndsAtUtc = metadata.EndsAtUtc?.UtcDateTime ?? summary.EndsAtUtc;
            summary.MaxAttempts = metadata.MaxAttempts;
            summary.ProctoringEnabled = metadata.ProctoringEnabled;
            if (sessionState.Current.UserId is Guid candidateId)
                await databaseService.UpdateCachedExamMetadataAsync(candidateId, summary.Id,
                    metadata.MaxAttempts, metadata.ProctoringEnabled, cancellationToken);
            return metadata;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    public async Task<Exam?> TryLoadInstalledExamAsync(Exam summary,
        CancellationToken cancellationToken = default)
    {
        DownloadedExamRecord? record = await databaseService.GetDownloadedExamAsync(
            summary.Id, cancellationToken);
        if (record is null || !string.Equals(record.State, "Ready", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(summary.PackageVersion, out long expectedVersion)
            || record.PackageVersion != expectedVersion
            || !string.Equals(record.ContentHash, summary.PackageHash, StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            byte[] packageBytes = await packageStore.ReadAsync(record.LocalPath, cancellationToken);
            string actualHash = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, record.ContentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed exam package failed integrity verification.");
            using var stream = new MemoryStream(packageBytes, writable: false);
            ExamPackageV1 package = ExamPackageValidator.ParseCandidateSafePackage(stream, summary.Id);
            return BuildExam(summary, package, package.Exam.Title, package.Exam.Description,
                package.Exam.DurationMinutes, package.Exam.StartsAt, package.Exam.EndsAt,
                Math.Max(1, summary.MaxAttempts), package.Exam.ProctoringEnabled,
                record.PackageVersion, record.ContentHash);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or System.Security.Cryptography.CryptographicException or FormatException)
        {
            await databaseService.SetDownloadedExamStateAsync(summary.Id, "Corrupt", cancellationToken);
            System.Diagnostics.Debug.WriteLine($"Installed exam package rejected: {ex}");
            return null;
        }
    }

    private static Exam BuildExam(Exam summary, ExamPackageV1 package, string title,
        string? description, int? durationMinutes, DateTimeOffset? startsAtUtc,
        DateTimeOffset? endsAtUtc, int maxAttempts, bool proctoringEnabled,
        long packageVersion, string packageHash) => new()
        {
            Id = summary.Id,
            Title = title,
            Description = description ?? string.Empty,
            DurationMinutes = durationMinutes ?? summary.DurationMinutes,
            StartsAtUtc = startsAtUtc?.UtcDateTime ?? summary.StartsAtUtc,
            EndsAtUtc = endsAtUtc?.UtcDateTime ?? summary.EndsAtUtc,
            MaxAttempts = maxAttempts,
            ProctoringEnabled = proctoringEnabled,
            PackageVersion = packageVersion.ToString(),
            PackageHash = packageHash,
            PackageSizeBytes = summary.PackageSizeBytes,
            CanDownload = summary.CanDownload,
            CanStartOffline = summary.CanStartOffline,
            ExistingAttemptStatus = summary.ExistingAttemptStatus,
            Questions = package.Sections.OrderBy(x => x.SortOrder)
                .SelectMany(x => x.Questions.OrderBy(q => q.SortOrder))
                .Select(MapQuestion).ToList(),
            ShuffleQuestions = package.Exam.ShuffleQuestions,
            ShuffleOptions = package.Exam.ShuffleOptions
        };

    private static void ValidateMetadata(ExamMetadataResponse metadata, Guid expectedExamId)
    {
        if (metadata.Id != expectedExamId || string.IsNullOrWhiteSpace(metadata.Title)
            || metadata.MaxAttempts <= 0 || metadata.DurationMinutes is <= 0
            || metadata.UpdatedAt == default
            || metadata.StartsAtUtc is { } starts && metadata.EndsAtUtc is { } ends && ends <= starts)
            throw new InvalidDataException("Examifo returned invalid exam metadata.");
    }

    private static AvailableExamRecord ToRecord(Exam exam) => new()
    {
        ExamId = exam.Id, Title = exam.Title, DurationMinutes = exam.DurationMinutes,
        StartsAtUtc = exam.StartsAtUtc, EndsAtUtc = exam.EndsAtUtc,
        PackageVersion = long.Parse(exam.PackageVersion), PackageHash = exam.PackageHash,
        PackageSizeBytes = exam.PackageSizeBytes, CanDownload = exam.CanDownload,
        MaxAttempts = exam.MaxAttempts, ProctoringEnabled = exam.ProctoringEnabled,
        CanStartOffline = exam.CanStartOffline, ExistingAttemptStatus = exam.ExistingAttemptStatus
    };

    private static Exam FromRecord(AvailableExamRecord record) => new()
    {
        Id = record.ExamId, Title = record.Title, DurationMinutes = record.DurationMinutes,
        StartsAtUtc = record.StartsAtUtc, EndsAtUtc = record.EndsAtUtc,
        PackageVersion = record.PackageVersion.ToString(), PackageHash = record.PackageHash,
        PackageSizeBytes = record.PackageSizeBytes, CanDownload = record.CanDownload,
        MaxAttempts = record.MaxAttempts, ProctoringEnabled = record.ProctoringEnabled,
        CanStartOffline = record.CanStartOffline, ExistingAttemptStatus = record.ExistingAttemptStatus
    };

    private static Question MapQuestion(ExamPackageQuestionItem item) => new()
    {
        Id = item.QuestionId,
        ExamQuestionId = item.ExamQuestionId,
        Prompt = item.Question.Body,
        QuestionType = MapQuestionType(item.Question.QuestionType),
        Marks = item.Marks,
        NegativeMarks = item.NegativeMarks,
        IsRequired = item.IsRequired,
        Difficulty = item.Question.Difficulty,
        DefaultTimeSec = item.Question.DefaultTimeSec ?? 0,
        SettingsJson = item.Question.SettingsJson?.GetRawText() ?? string.Empty,
        Options = (item.Question.Options ?? []).OrderBy(x => x.SortOrder)
            .Select(x => new QuestionOption { Id = x.Id, Text = x.Body, IsCorrect = false }).ToList()
    };

    private static QuestionType MapQuestionType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "mcq" or "multiple_choice" or "multiplechoice" or "single_choice" or "singlechoice" => QuestionType.SingleChoice,
        "multiple_select" or "multipleselect" => QuestionType.MultipleSelect,
        "true_false" or "truefalse" or "boolean" => QuestionType.TrueFalse,
        "essay" => QuestionType.Essay,
        "math" or "equation" or "equations" or "text_equation" or "text_equations"
            or "rich_answer" or "richanswer" => QuestionType.Math,
        "drawing" or "diagram" or "drawing_diagram" => QuestionType.Drawing,
        "multi_part" or "multipart" or "multi_part_question" or "composite_answer" or "compositeanswer"
            => QuestionType.MultiPart,
        "table_grid" or "tablegrid" or "table" or "grid" or "grid_answer" or "gridanswer"
            => QuestionType.TableGrid,
        "code" or "coding" or "coding_question" => QuestionType.Coding,
        _ => QuestionType.ShortAnswer
    };
}
