using System.Security.Cryptography;
using System.Text;
using Examifo_Desktop.Domain.Models;

namespace Examifo_Desktop.Services;

public static class DeterministicExamOrder
{
    public static void Apply(Exam exam, string shuffleSeed)
    {
        if (string.IsNullOrWhiteSpace(shuffleSeed))
            throw new InvalidOperationException("The attempt ordering seed is unavailable.");
        if (exam.ShuffleQuestions)
            exam.Questions = exam.Questions.OrderBy(x => Digest(shuffleSeed, x.ExamQuestionId),
                ByteArrayComparer.Instance).ToList();
        if (exam.ShuffleOptions)
            foreach (Question question in exam.Questions)
                question.Options = question.Options.OrderBy(x => Digest(shuffleSeed, x.Id),
                    ByteArrayComparer.Instance).ToList();
    }

    private static byte[] Digest(string seed, Guid id) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{id:D}"));

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y) =>
            (x, y) switch
            {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                _ => x.AsSpan().SequenceCompareTo(y)
            };
    }
}
