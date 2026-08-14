using SQLite;

namespace Examifo_Desktop.Domain.Models;

public class Answer
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AttemptId { get; set; }

    public Guid QuestionId { get; set; }

    public string Response { get; set; } = string.Empty;

    public DateTime AnsweredAtUtc { get; set; } = DateTime.UtcNow;
}