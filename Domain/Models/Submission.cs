using SQLite;

namespace Examifo_Desktop.Domain.Models;

public class Submission
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AttemptId { get; set; }

    public DateTime CreatedAtUtc { get; set; } =
        DateTime.UtcNow;

    public string Status { get; set; } =
        "Pending";

    public int Score { get; set; }

    public int TotalQuestions { get; set; }

    public string ResultStatus { get; set; } = "pending_sync";
    public decimal? ScoreTotal { get; set; }
    public decimal? ScoreObtained { get; set; }
    public decimal? Percentage { get; set; }
    public bool? Passed { get; set; }
    public DateTime? AuthoritativeSubmittedAtUtc { get; set; }
    public DateTime? ResultUpdatedAtUtc { get; set; }
}
