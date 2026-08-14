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
}