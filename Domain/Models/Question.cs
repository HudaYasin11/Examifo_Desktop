using Examifo_Desktop.Domain.Enums;

namespace Examifo_Desktop.Domain.Models;

public class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ExamQuestionId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public QuestionType QuestionType { get; set; }

    public decimal Marks { get; set; }

    public decimal NegativeMarks { get; set; }

    public bool IsRequired { get; set; }

    public string Difficulty { get; set; } = string.Empty;

    public int DefaultTimeSec { get; set; }

    public List<QuestionOption> Options { get; set; } = new();
}
