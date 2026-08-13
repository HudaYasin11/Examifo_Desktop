namespace Examifo_Desktop.Domain.Models;

public class QuestionOption
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = string.Empty;

    public bool IsCorrect { get; set; }
}