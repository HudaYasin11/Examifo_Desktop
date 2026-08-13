namespace Examifo_Desktop.Domain.Models;

public class Submission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AttemptId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string Status { get; set; } = "Pending";
}