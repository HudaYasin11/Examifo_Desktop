using Examifo_Desktop.Domain.Models;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Services;

public static class AvailableExamMapper
{
    public static List<Exam> Map(AvailableExamsResponse catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        if (catalogue.ServerTimeUtc == default || catalogue.Exams is null)
            throw new InvalidDataException("Examifo returned an invalid assigned-exam catalogue.");

        var seen = new HashSet<Guid>();
        var result = new List<Exam>(catalogue.Exams.Count);
        foreach (AvailableExamItem item in catalogue.Exams)
        {
            Validate(item);
            if (!seen.Add(item.ExamId))
                throw new InvalidDataException("Examifo returned a duplicate assigned exam.");
            result.Add(new Exam
            {
                Id = item.ExamId,
                Title = item.Title.Trim(),
                DurationMinutes = item.DurationMinutes ?? 0,
                StartsAtUtc = item.StartsAtUtc?.UtcDateTime ?? DateTime.MinValue,
                EndsAtUtc = item.EndsAtUtc?.UtcDateTime ?? DateTime.MaxValue,
                PackageVersion = item.PackageVersion.ToString(),
                PackageHash = item.PackageHash,
                PackageSizeBytes = item.PackageSizeBytes,
                CanDownload = item.CanDownload,
                CanStartOffline = item.CanStartOffline,
                ExistingAttemptStatus = NormalizeStatus(item.ExistingAttemptStatus)
            });
        }
        return result;
    }

    private static void Validate(AvailableExamItem item)
    {
        if (item.ExamId == Guid.Empty || string.IsNullOrWhiteSpace(item.Title)
            || item.PackageVersion <= 0 || item.PackageSizeBytes < 0
            || item.PackageHash.Length != 64 || !item.PackageHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Examifo returned an invalid assigned exam.");
        if (item.DurationMinutes is < 0)
            throw new InvalidDataException("Examifo returned an invalid exam duration.");
        if (item.StartsAtUtc is { } starts && item.EndsAtUtc is { } ends && ends <= starts)
            throw new InvalidDataException("Examifo returned an invalid exam availability window.");
    }

    private static string? NormalizeStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToLowerInvariant();
}
