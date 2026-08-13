namespace Examifo_Desktop.Domain.Models;

public class Exam
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int DurationMinutes { get; set; }

    public DateTime StartsAtUtc { get; set; }

    public DateTime EndsAtUtc { get; set; }

    public int MaxAttempts { get; set; }

    public bool ProctoringEnabled { get; set; }

    public string PackageVersion { get; set; } = string.Empty;

    public string PackageHash { get; set; } = string.Empty;

    public List<Question> Questions { get; set; } = new();
}