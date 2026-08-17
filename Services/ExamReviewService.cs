using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Services;

public sealed record ExamReviewItem(
    int Index, Guid QuestionId, string Prompt, bool IsRequired, bool IsAnswered);

public sealed record ExamReviewSummary(IReadOnlyList<ExamReviewItem> Questions)
{
    public int AnsweredCount => Questions.Count(x => x.IsAnswered);
    public int MissingRequiredCount => Questions.Count(x => x.IsRequired && !x.IsAnswered);
    public bool CanSubmit => MissingRequiredCount == 0;
}

public static class ExamReviewService
{
    public static ExamReviewSummary Build(Exam exam, Func<Question, bool> hasAnswer)
    {
        ArgumentNullException.ThrowIfNull(exam);
        ArgumentNullException.ThrowIfNull(hasAnswer);
        return new ExamReviewSummary(exam.Questions.Select((question, index) =>
            new ExamReviewItem(index, question.Id, question.Prompt,
                question.IsRequired, hasAnswer(question))).ToArray());
    }
}
