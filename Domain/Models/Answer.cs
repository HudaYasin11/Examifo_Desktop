namespace Examifo_Desktop.Domain.Models;

public class Answer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid QuestionId { get; set; }

    public string Response { get; set; } = string.Empty;

    public DateTime AnsweredAtUtc { get; set; } = DateTime.UtcNow;
}