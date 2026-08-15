using SQLite;
using Examifo_Desktop.Domain.Enums;

namespace Examifo_Desktop.Domain.Models;

public class Attempt
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ExamId { get; set; }

    public Guid AuthorizationId { get; set; }

    public Guid DeviceId { get; set; }

    public long PackageVersion { get; set; }

    public long NextSequence { get; set; } = 1;

    public AttemptStatus Status { get; set; } = AttemptStatus.Authorized;

    public DateTime StartedAtUtc { get; set; }

    public DateTime DeadlineUtc { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }

    public int CurrentQuestionIndex { get; set; }

    public DateTime LastActivityUtc { get; set; }

    [Ignore]
    public List<Answer> Answers { get; set; } = new();
}
