using System.Text.Json;

namespace Examifo_Desktop.Infrastructure.Api.DTOs;

public sealed record AvailableExamsResponse(
    DateTimeOffset ServerTimeUtc,
    List<AvailableExamItem> Exams);

public sealed record AvailableExamItem(
    Guid ExamId,
    string Title,
    int? DurationMinutes,
    DateTimeOffset? StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    long PackageVersion,
    string PackageHash,
    long PackageSizeBytes,
    bool CanDownload,
    bool CanStartOffline,
    string? ExistingAttemptStatus);

public sealed record ExamMetadataResponse(
    Guid Id,
    string Title,
    string? Description,
    int? DurationMinutes,
    DateTimeOffset? StartsAtUtc,
    DateTimeOffset? EndsAtUtc,
    int MaxAttempts,
    bool ProctoringEnabled,
    DateTimeOffset UpdatedAt);

public sealed record PackageManifestResponse(
    Guid PackageId,
    Guid ExamId,
    long Version,
    string ContentHash,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    string MinimumAppVersion,
    string DownloadUrl);

public sealed record ExamPackageV1(
    int SchemaVersion,
    ExamPackageDetails Exam,
    List<ExamPackageSection> Sections);

public sealed record ExamPackageDetails(
    Guid Id,
    string Title,
    string? Description,
    int? DurationMinutes,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string QuestionDisplayMode,
    bool ShuffleQuestions,
    bool ShuffleOptions,
    bool ProctoringEnabled);

public sealed record ExamPackageSection(
    Guid Id,
    string Title,
    int SortOrder,
    List<ExamPackageQuestionItem> Questions);

public sealed record ExamPackageQuestionItem(
    Guid ExamQuestionId,
    Guid QuestionId,
    int SortOrder,
    decimal Marks,
    decimal NegativeMarks,
    bool IsRequired,
    ExamPackageQuestion Question);

public sealed record ExamPackageQuestion(
    string QuestionType,
    string Difficulty,
    string Body,
    int? DefaultTimeSec,
    JsonElement? SettingsJson,
    List<ExamPackageOption>? Options);

public sealed record ExamPackageOption(Guid Id, string Body, int SortOrder);
