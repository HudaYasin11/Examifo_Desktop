using SQLite;

namespace Examifo_Desktop.Domain.Models;

public class Answer
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AttemptId { get; set; }

    public Guid QuestionId { get; set; }

    public Guid ExamQuestionId { get; set; }

    public Guid? SelectedOptionId { get; set; }

    public string ResponseFormat { get; set; } = "selected_options";

    public string Response { get; set; } = string.Empty;

    public long Revision { get; set; } = 1;

    public DateTime AnsweredAtUtc { get; set; } = DateTime.UtcNow;
}
