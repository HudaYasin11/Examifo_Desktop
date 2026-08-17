using System.Net;
using System.Text;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Services;
using Examifo_Desktop.Infrastructure.Persistence;
using Examifo_Desktop.Infrastructure.Security;

Guid examId = Guid.NewGuid();
string hash = new('a', 64);
var item = new AvailableExamItem(examId, "Assigned Exam", 60, DateTimeOffset.UtcNow,
    DateTimeOffset.UtcNow.AddDays(1), 4, hash, 128, true, true, null);
List<Examifo_Desktop.Domain.Models.Exam> mapped = AvailableExamMapper.Map(
    new AvailableExamsResponse(DateTimeOffset.UtcNow, [item]));
Assert(mapped.Count == 1 && mapped[0].Id == examId && mapped[0].PackageVersion == "4",
    "available catalogue validates and maps assigned exams");

Guid[] questionIds =
[
    Guid.Parse("10000000-0000-0000-0000-000000000001"),
    Guid.Parse("10000000-0000-0000-0000-000000000002"),
    Guid.Parse("10000000-0000-0000-0000-000000000003")
];
Examifo_Desktop.Domain.Models.Exam orderedA = CreateOrderingExam(questionIds);
Examifo_Desktop.Domain.Models.Exam orderedB = CreateOrderingExam(questionIds.Reverse().ToArray());
DeterministicExamOrder.Apply(orderedA, "attempt-seed");
DeterministicExamOrder.Apply(orderedB, "attempt-seed");
Assert(orderedA.Questions.Select(x => x.ExamQuestionId)
        .SequenceEqual(orderedB.Questions.Select(x => x.ExamQuestionId)) &&
    orderedA.Questions.Zip(orderedB.Questions).All(pair =>
        pair.First.Options.Select(x => x.Id).SequenceEqual(pair.Second.Options.Select(x => x.Id))),
    "deterministic seed produces stable question and option ordering");

var reviewExam = new Examifo_Desktop.Domain.Models.Exam
{
    Questions =
    [
        new() { Id = Guid.NewGuid(), Prompt = "Required answered", IsRequired = true },
        new() { Id = Guid.NewGuid(), Prompt = "Required missing", IsRequired = true },
        new() { Id = Guid.NewGuid(), Prompt = "Optional", IsRequired = false }
    ]
};
Guid answeredQuestionId = reviewExam.Questions[0].Id;
ExamReviewSummary blockedReview = ExamReviewService.Build(reviewExam,
    question => question.Id == answeredQuestionId);
Assert(blockedReview.AnsweredCount == 1 && blockedReview.MissingRequiredCount == 1
    && !blockedReview.CanSubmit && blockedReview.Questions[1].Index == 1,
    "review summary blocks submission and identifies unanswered required questions");
ExamReviewSummary completeReview = ExamReviewService.Build(reviewExam,
    question => question.IsRequired);
Assert(completeReview.CanSubmit && completeReview.AnsweredCount == 2,
    "review summary allows submission when every required answer is present");

bool duplicateRejected = false;
try { AvailableExamMapper.Map(new AvailableExamsResponse(DateTimeOffset.UtcNow, [item, item])); }
catch (InvalidDataException) { duplicateRejected = true; }
Assert(duplicateRejected, "duplicate catalogue entries are rejected");

var manifest = new PackageManifestResponse(Guid.NewGuid(), examId, 4, hash, 128,
    DateTimeOffset.UtcNow, "1.0", "/api/v1/package");
ExamPackageValidator.ValidateManifest(manifest, examId, 4, hash, 128, new Version(1, 0));
Assert(true, "matching package manifest is accepted");
bool mismatchRejected = false;
try { ExamPackageValidator.ValidateManifest(manifest with { ExamId = Guid.NewGuid() },
    examId, 4, hash, 128, new Version(1, 0)); }
catch (InvalidDataException) { mismatchRejected = true; }
Assert(mismatchRejected, "mismatched package manifest is rejected");
bool minimumVersionRejected = false;
try { ExamPackageValidator.ValidateManifest(manifest with { MinimumAppVersion = "99.0" },
    examId, 4, hash, 128, new Version(1, 0)); }
catch (InvalidDataException) { minimumVersionRejected = true; }
Assert(minimumVersionRejected, "package requiring a newer application is rejected");

string safeJson = $$"""
{
  "schemaVersion": 1,
  "exam": {
    "id": "{{examId}}", "title": "Safe", "description": "", "durationMinutes": 60,
    "startsAt": null, "endsAt": null, "questionDisplayMode": "all",
    "shuffleQuestions": false, "shuffleOptions": false, "proctoringEnabled": false
  },
  "sections": [{
    "id": "{{Guid.NewGuid()}}", "title": "A", "sortOrder": 1,
    "questions": [{
      "examQuestionId": "{{Guid.NewGuid()}}", "questionId": "{{Guid.NewGuid()}}",
      "sortOrder": 1, "marks": 1, "negativeMarks": 0, "isRequired": true,
      "question": {
        "questionType": "text", "difficulty": "easy", "body": "Prompt",
        "defaultTimeSec": null, "settingsJson": null, "options": null
      }
    }]
  }]
}
""";
using (var safe = new MemoryStream(Encoding.UTF8.GetBytes(safeJson)))
    Assert(ExamPackageValidator.ParseCandidateSafePackage(safe, examId).SchemaVersion == 1,
        "candidate-safe package is accepted");
foreach (string websiteType in new[] { "text_equations", "rich_answer", "drawing_diagram",
             "multi_part_question", "composite_answer", "table_grid", "grid_answer", "coding_question" })
{
    using var websitePackage = new MemoryStream(Encoding.UTF8.GetBytes(
        safeJson.Replace("\"questionType\": \"text\"",
            $"\"questionType\": \"{websiteType}\"")));
    Assert(ExamPackageValidator.ParseCandidateSafePackage(websitePackage, examId).SchemaVersion == 1,
        $"website question alias {websiteType} is accepted");
}
bool unsupportedTypeNamed = false;
using (var unsupportedPackage = new MemoryStream(Encoding.UTF8.GetBytes(
    safeJson.Replace("\"questionType\": \"text\"", "\"questionType\": \"file_upload\""))))
try { ExamPackageValidator.ParseCandidateSafePackage(unsupportedPackage, examId); }
catch (InvalidDataException ex)
{
    unsupportedTypeNamed = ex.Message.Contains("file_upload", StringComparison.Ordinal);
}
Assert(unsupportedTypeNamed, "unsupported package diagnostics identify the exact question type");
bool protectedRejected = false;
using (var unsafeStream = new MemoryStream(Encoding.UTF8.GetBytes(
    safeJson.Replace("\"body\": \"Prompt\"",
        "\"body\": \"Prompt\", \"correctAnswer\": \"secret\""))))
try { ExamPackageValidator.ParseCandidateSafePackage(unsafeStream, examId); }
catch (InvalidDataException) { protectedRejected = true; }
Assert(protectedRejected, "protected grading content is rejected");
bool futureSchemaRejected = false;
using (var futureSchema = new MemoryStream(Encoding.UTF8.GetBytes(
    safeJson.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"))))
try { ExamPackageValidator.ParseCandidateSafePackage(futureSchema, examId); }
catch (InvalidDataException) { futureSchemaRejected = true; }
Assert(futureSchemaRejected, "unknown package schema version is rejected");

bool duplicateSchemaRejected = false;
string duplicateSchema = safeJson.Replace("\"questions\": [{", "\"questions\": [{")
    .Replace("\"sections\": [{", "\"sections\": [{");
using (var invalidSchema = new MemoryStream(Encoding.UTF8.GetBytes(
    duplicateSchema.Replace("\"sortOrder\": 1,\n    \"questions\"",
        "\"sortOrder\": 1,\n    \"questions\""))))
{
    // A wrong expected exam ID exercises identity/schema binding without accepting the document.
    try { ExamPackageValidator.ParseCandidateSafePackage(invalidSchema, Guid.NewGuid()); }
    catch (InvalidDataException) { duplicateSchemaRejected = true; }
}
Assert(duplicateSchemaRejected, "schema and exam identity mismatch is rejected");

byte[] payload = Encoding.UTF8.GetBytes("streamed-package");
var downloadHandler = new StaticHandler(payload);
var client = new ExamApiClient(new AuthenticatedHttpClient(
    new HttpClient(downloadHandler) { BaseAddress = new Uri("https://examifo.test/") },
    new StaticTokenProvider()));
using var destination = new MemoryStream();
await client.DownloadPackageAsync("api/v1/package", destination, payload.Length);
Assert(destination.ToArray().SequenceEqual(payload), "package download streams to its destination");
bool oversizedRejected = false;
try { await client.DownloadPackageAsync("api/v1/package", new MemoryStream(), payload.Length - 1); }
catch (InvalidDataException) { oversizedRejected = true; }
Assert(oversizedRejected, "download exceeding the manifest limit is rejected");
var notModifiedHandler = new StaticHandler(payload, HttpStatusCode.NotModified);
var conditionalClient = new ExamApiClient(new AuthenticatedHttpClient(
    new HttpClient(notModifiedHandler) { BaseAddress = new Uri("https://examifo.test/") },
    new StaticTokenProvider()));
PackageDownloadResult conditionalResult = await conditionalClient.DownloadPackageAsync(
    "api/v1/package", new MemoryStream(), payload.Length, new string('a', 64));
Assert(conditionalResult == PackageDownloadResult.NotModified
    && notModifiedHandler.IfNoneMatch == $"\"{new string('a', 64)}\"",
    "conditional package request sends If-None-Match and accepts HTTP 304");

string packageRoot = Path.Combine(Path.GetTempPath(), "examifo-package-tests", Guid.NewGuid().ToString("N"));
try
{
    var pathProvider = new TestPackagePaths(packageRoot);
    var packageStore = new ExamPackageStore(
        new EncryptionService(new MemorySecureValueStore()), pathProvider);
    byte[] packageBytes = Encoding.UTF8.GetBytes(safeJson);
    string contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        packageBytes)).ToLowerInvariant();
    PackageInstallation version1 = await packageStore.InstallAsync(examId, 1, contentHash, packageBytes);
    string rawInstalled = await File.ReadAllTextAsync(version1.LocalPath);
    Assert(rawInstalled.StartsWith("enc:v1:") && !rawInstalled.Contains("Prompt")
        && (await packageStore.ReadAsync(version1.LocalPath)).SequenceEqual(packageBytes),
        "installed package is encrypted and readable after secure installation");
    PackageInstallation[] concurrent = await Task.WhenAll(Enumerable.Range(0, 8)
        .Select(_ => packageStore.InstallAsync(examId, 1, contentHash, packageBytes)));
    Assert(concurrent.All(x => x.LocalPath == version1.LocalPath)
        && Directory.GetFiles(Path.GetDirectoryName(version1.LocalPath)!, "*.examifo").Length == 1,
        "concurrent package installation converges on one immutable artifact");
    string secondJson = safeJson.Replace("Prompt", "Updated prompt");
    byte[] secondBytes = Encoding.UTF8.GetBytes(secondJson);
    string secondHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        secondBytes)).ToLowerInvariant();
    PackageInstallation version2 = await packageStore.InstallAsync(examId, 2, secondHash, secondBytes);
    Assert(File.Exists(version1.LocalPath) && File.Exists(version2.LocalPath),
        "new package installation preserves the previous known-good version until activation");

    Directory.CreateDirectory(pathProvider.TemporaryPackageDirectory);
    string abandonedDownload = Path.Combine(pathProvider.TemporaryPackageDirectory, "crash.download");
    string abandonedInstall = Path.Combine(pathProvider.InstalledPackageDirectory, "crash.installing");
    await File.WriteAllTextAsync(abandonedDownload, "partial");
    await File.WriteAllTextAsync(abandonedInstall, "partial");
    packageStore.CleanupAbandonedFiles();
    Assert(!File.Exists(abandonedDownload) && !File.Exists(abandonedInstall)
        && File.Exists(version1.LocalPath) && File.Exists(version2.LocalPath),
        "restart cleanup removes abandoned staging files without deleting installed packages");

    await File.AppendAllTextAsync(version2.LocalPath, "tampered");
    bool corruptionRejected = false;
    try { await packageStore.ReadAsync(version2.LocalPath); }
    catch (System.Security.Cryptography.CryptographicException) { corruptionRejected = true; }
    Assert(corruptionRejected, "tampered encrypted package is rejected during restart loading");
}
finally
{
    try { if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, recursive: true); }
    catch (IOException) { }
}
Console.WriteLine("All exam acquisition tests passed.");

static Examifo_Desktop.Domain.Models.Exam CreateOrderingExam(IEnumerable<Guid> ids) => new()
{
    ShuffleQuestions = true,
    ShuffleOptions = true,
    Questions = ids.Select(id => new Examifo_Desktop.Domain.Models.Question
    {
        Id = id,
        ExamQuestionId = id,
        Options =
        [
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000002"), Text = "B" },
            new() { Id = Guid.Parse("20000000-0000-0000-0000-000000000001"), Text = "A" }
        ]
    }).ToList()
};

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}

sealed class StaticTokenProvider : IAuthenticatedTokenProvider
{
    public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("token");
    public Task<string> RefreshAfterUnauthorizedAsync(string rejectedAccessToken,
        CancellationToken cancellationToken = default) => Task.FromResult("token-2");
}

sealed class StaticHandler(byte[] content, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    public string? IfNoneMatch { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        IfNoneMatch = request.Headers.IfNoneMatch.SingleOrDefault()?.ToString();
        return Task.FromResult(new HttpResponseMessage(statusCode)
            { Content = new ByteArrayContent(content) });
    }
}

sealed class TestPackagePaths(string root) : ILocalPackagePathProvider
{
    public string TemporaryPackageDirectory { get; } = Path.Combine(root, "temporary");
    public string InstalledPackageDirectory { get; } = Path.Combine(root, "installed");
}

sealed class MemorySecureValueStore : ISecureValueStore
{
    private readonly Dictionary<string, string> _values = [];
    public Task<string?> GetAsync(string key) =>
        Task.FromResult(_values.TryGetValue(key, out string? value) ? value : null);
    public Task SetAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
    public void Remove(string key) => _values.Remove(key);
}
