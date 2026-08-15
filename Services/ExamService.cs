using System.Security.Cryptography;
using System.Text.Json;
using Examifo_Desktop.Domain.Enums;
using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Services;

public sealed class ExamService(ExamApiClient examApiClient, TrustedServerTimeService trustedTime)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<Exam>> GetAvailableExamsAsync(
        DateTimeOffset? modifiedSinceUtc = null,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset requestStarted = DateTimeOffset.UtcNow;
        AvailableExamsResponse catalogue = await examApiClient.GetAvailableExamsAsync(
            modifiedSinceUtc, cancellationToken);
        trustedTime.RecordSample(catalogue.ServerTimeUtc, requestStarted, DateTimeOffset.UtcNow);
        return AvailableExamMapper.Map(catalogue);
    }

    public async Task<Exam> PrepareExamAsync(Exam summary, CancellationToken cancellationToken = default)
    {
        if (!summary.CanDownload && summary.Questions.Count == 0)
            throw new InvalidOperationException("This assigned exam is not currently available for download.");
        ExamMetadataResponse metadata = await examApiClient.GetExamAsync(summary.Id, cancellationToken);
        PackageManifestResponse manifest = await examApiClient.GetManifestAsync(summary.Id, cancellationToken);
        byte[] bytes = await examApiClient.DownloadPackageAsync(manifest.DownloadUrl, cancellationToken);

        string actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, manifest.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Exam package integrity verification failed.");

        ExamPackageV1 package = JsonSerializer.Deserialize<ExamPackageV1>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Examifo returned an invalid exam package.");
        if (package.SchemaVersion != 1 || package.Exam.Id != summary.Id)
            throw new InvalidDataException("Unsupported or mismatched exam package.");

        return new Exam
        {
            Id = summary.Id,
            Title = metadata.Title,
            Description = metadata.Description ?? string.Empty,
            DurationMinutes = metadata.DurationMinutes ?? summary.DurationMinutes,
            StartsAtUtc = metadata.StartsAtUtc?.UtcDateTime ?? summary.StartsAtUtc,
            EndsAtUtc = metadata.EndsAtUtc?.UtcDateTime ?? summary.EndsAtUtc,
            MaxAttempts = metadata.MaxAttempts,
            ProctoringEnabled = metadata.ProctoringEnabled,
            PackageVersion = manifest.Version.ToString(),
            PackageHash = manifest.ContentHash,
            Questions = package.Sections.OrderBy(x => x.SortOrder)
                .SelectMany(x => x.Questions.OrderBy(q => q.SortOrder))
                .Select(MapQuestion).ToList()
        };
    }

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
        Options = (item.Question.Options ?? []).OrderBy(x => x.SortOrder)
            .Select(x => new QuestionOption { Id = x.Id, Text = x.Body, IsCorrect = false }).ToList()
    };

    private static QuestionType MapQuestionType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "mcq" or "single_choice" or "singlechoice" => QuestionType.SingleChoice,
        "multiple_select" or "multipleselect" => QuestionType.MultipleSelect,
        "true_false" or "truefalse" => QuestionType.TrueFalse,
        "essay" => QuestionType.Essay,
        "math" => QuestionType.Math,
        "drawing" => QuestionType.Drawing,
        "multi_part" or "multipart" => QuestionType.MultiPart,
        "table_grid" or "tablegrid" => QuestionType.TableGrid,
        "code" or "coding" => QuestionType.Coding,
        _ => QuestionType.ShortAnswer
    };
}
