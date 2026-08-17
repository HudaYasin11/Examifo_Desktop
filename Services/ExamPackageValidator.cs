using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Services;

public static class ExamPackageValidator
{
    private static readonly HashSet<string> ForbiddenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "isCorrect", "correctAnswer", "correctAnswers", "correctOptionId", "correctOptionIds",
        "answerKey", "modelAnswer", "rubric", "explanation", "gradingInstructions",
        "hiddenTests", "hiddenTestCases", "expectedOutput", "solution"
    };

    public static void ValidateManifest(PackageManifestResponse manifest, Guid expectedExamId,
        long expectedVersion, string expectedHash, long expectedSize, Version currentAppVersion)
    {
        if (manifest.PackageId == Guid.Empty || manifest.ExamId != expectedExamId
            || manifest.Version <= 0 || manifest.Version != expectedVersion
            || manifest.SizeBytes <= 0 || manifest.SizeBytes != expectedSize
            || string.IsNullOrWhiteSpace(manifest.DownloadUrl)
            || !Uri.TryCreate(manifest.DownloadUrl, UriKind.RelativeOrAbsolute, out _)
            || manifest.ContentHash.Length != 64 || !manifest.ContentHash.All(Uri.IsHexDigit)
            || !string.Equals(manifest.ContentHash, expectedHash, StringComparison.OrdinalIgnoreCase)
            || !Version.TryParse(manifest.MinimumAppVersion, out Version? minimumVersion)
            || Normalize(currentAppVersion) < Normalize(minimumVersion))
            throw new InvalidDataException("Examifo returned an invalid or unsupported package manifest.");
    }

    private static Version Normalize(Version version) => new(
        version.Major,
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    public static ExamPackageV1 ParseCandidateSafePackage(Stream stream, Guid expectedExamId)
    {
        using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 64
        });
        RejectProtectedContent(document.RootElement);
        ExamPackageV1 package = document.RootElement.Deserialize<ExamPackageV1>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Examifo returned an invalid exam package.");
        if (package.SchemaVersion != 1 || package.Exam is null || package.Exam.Id != expectedExamId
            || package.Sections is null || package.Sections.Count == 0)
            throw new InvalidDataException("Unsupported, empty, or mismatched exam package.");
        ValidateSchema(package);
        return package;
    }

    private static void ValidateSchema(ExamPackageV1 package)
    {
        if (string.IsNullOrWhiteSpace(package.Exam.Title) || package.Exam.DurationMinutes is <= 0)
            throw new InvalidDataException("The exam package contains invalid exam metadata.");
        var sectionIds = new HashSet<Guid>();
        var sectionOrders = new HashSet<int>();
        var examQuestionIds = new HashSet<Guid>();
        var questionIds = new HashSet<Guid>();
        foreach (ExamPackageSection section in package.Sections)
        {
            if (section.Id == Guid.Empty || string.IsNullOrWhiteSpace(section.Title)
                || !sectionIds.Add(section.Id) || !sectionOrders.Add(section.SortOrder)
                || section.Questions is null || section.Questions.Count == 0)
                throw new InvalidDataException("The exam package contains an invalid or duplicate section.");
            var questionOrders = new HashSet<int>();
            foreach (ExamPackageQuestionItem item in section.Questions)
            {
                if (item.ExamQuestionId == Guid.Empty || item.QuestionId == Guid.Empty
                    || !examQuestionIds.Add(item.ExamQuestionId) || !questionIds.Add(item.QuestionId)
                    || !questionOrders.Add(item.SortOrder) || item.Marks < 0 || item.NegativeMarks < 0
                    || item.Question is null || string.IsNullOrWhiteSpace(item.Question.Body)
                    || string.IsNullOrWhiteSpace(item.Question.QuestionType))
                    throw new InvalidDataException("The exam package contains an invalid or duplicate question.");
                ValidateQuestion(item.Question);
            }
        }
    }

    private static void ValidateQuestion(ExamPackageQuestion question)
    {
        string type = question.QuestionType.Trim().ToLowerInvariant();
        string[] supported = ["mcq", "multiple_choice", "multiplechoice", "single_choice", "singlechoice",
            "multiple_select", "multipleselect", "true_false", "truefalse", "boolean",
            "text", "short_answer", "shortanswer", "essay", "math", "equation", "equations",
            "text_equation", "text_equations", "rich_answer", "richanswer",
            "drawing", "diagram", "drawing_diagram",
            "multi_part", "multipart", "multi_part_question", "composite_answer", "compositeanswer",
            "table_grid", "tablegrid",
            "table", "grid", "grid_answer", "gridanswer",
            "code", "coding", "coding_question"];
        if (!supported.Contains(type))
            throw new InvalidDataException(
                $"Unsupported question type '{question.QuestionType}'. Update the desktop app or edit this question.");
        if (question.DefaultTimeSec is < 0)
            throw new InvalidDataException(
                $"Question type '{question.QuestionType}' has an invalid negative time limit.");
        bool needsOptions = type is "mcq" or "multiple_choice" or "multiplechoice"
            or "single_choice" or "singlechoice" or
            "multiple_select" or "multipleselect" or "true_false" or "truefalse";
        List<ExamPackageOption> options = question.Options ?? [];
        if (needsOptions && options.Count < 2)
            throw new InvalidDataException(
                $"Question type '{question.QuestionType}' requires at least two answer options.");
        var optionIds = new HashSet<Guid>();
        var optionOrders = new HashSet<int>();
        if (options.Any(x => x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Body)
            || !optionIds.Add(x.Id) || !optionOrders.Add(x.SortOrder)))
            throw new InvalidDataException(
                $"Question type '{question.QuestionType}' contains an empty or duplicate answer option.");
    }

    private static void RejectProtectedContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (ForbiddenProperties.Contains(property.Name))
                    throw new InvalidDataException(
                        $"Candidate package contains protected grading content: {property.Name}.");
                RejectProtectedContent(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) RejectProtectedContent(item);
    }
}
