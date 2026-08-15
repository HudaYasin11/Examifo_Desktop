using System.Net;
using System.Text;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Api.Clients;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Services;

await TestDeviceLifecycleAsync();
await TestMismatchedInstallationRejectedAsync();
await TestIncrementalCatalogueRequestAsync();
TestEligibilityMapping();
TestInvalidCatalogueRejected();
Console.WriteLine("All device lifecycle and eligibility tests passed.");

static async Task TestDeviceLifecycleAsync()
{
    Guid installationId = Guid.NewGuid();
    Guid deviceId = Guid.NewGuid();
    var handler = new QueueHandler(
        Json(HttpStatusCode.OK, Device(deviceId, installationId)),
        Json(HttpStatusCode.OK, new[] { Device(deviceId, installationId) }),
        new HttpResponseMessage(HttpStatusCode.NoContent));
    var api = new DeviceApiClient(Client(handler));
    DeviceResponse registered = await api.RegisterOrUpdateAsync(
        new DeviceInput(installationId, "Candidate PC", "Windows", "1.0.0", null));
    IReadOnlyList<DeviceResponse> devices = await api.GetDevicesAsync();
    await api.RevokeAsync(deviceId);
    Assert(registered.Id == deviceId && devices.Single().InstallationId == installationId,
        "register/update and list preserve installation/device identity");
    Assert(handler.Requests.Select(x => $"{x.Method} {x.Path}").SequenceEqual([
        "POST /api/v1/devices", "GET /api/v1/devices", $"DELETE /api/v1/devices/{deviceId:D}"]),
        "device lifecycle uses contract routes");
    Assert(handler.Requests[0].Body.Contains(installationId.ToString(), StringComparison.OrdinalIgnoreCase),
        "registration sends the stable installation ID");
}

static async Task TestMismatchedInstallationRejectedAsync()
{
    Guid requested = Guid.NewGuid();
    var handler = new QueueHandler(Json(HttpStatusCode.OK, Device(Guid.NewGuid(), Guid.NewGuid())));
    var api = new DeviceApiClient(Client(handler));
    await ThrowsAsync<InvalidDataException>(() => api.RegisterOrUpdateAsync(
        new DeviceInput(requested, "PC", "Windows", "1.0", null)),
        "mismatched server installation is rejected");
}

static async Task TestIncrementalCatalogueRequestAsync()
{
    DateTimeOffset checkpoint = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    var handler = new QueueHandler(Json(HttpStatusCode.OK,
        new AvailableExamsResponse(checkpoint, [])));
    var api = new ExamApiClient(Client(handler));
    AvailableExamsResponse response = await api.GetAvailableExamsAsync(checkpoint);
    Assert(response.Exams.Count == 0, "empty assigned-exam catalogue is valid");
    Assert(handler.Requests.Single().Path.Contains("modifiedSinceUtc=", StringComparison.Ordinal),
        "incremental catalogue checkpoint is sent");
}

static void TestEligibilityMapping()
{
    Guid examId = Guid.NewGuid();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var response = new AvailableExamsResponse(now, [new AvailableExamItem(
        examId, " Assigned Exam ", 90, now.AddHours(1), now.AddHours(3), 42,
        new string('a', 64), 1234, true, false, " In_Progress ")]);
    var exam = AvailableExamMapper.Map(response).Single();
    Assert(exam.Id == examId && exam.CanDownload && !exam.CanStartOffline,
        "server eligibility flags are preserved");
    Assert(exam.ExistingAttemptStatus == "in_progress" && exam.PackageSizeBytes == 1234,
        "existing attempt and package metadata are preserved");
}

static void TestInvalidCatalogueRejected()
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var invalid = new AvailableExamsResponse(now, [new AvailableExamItem(
        Guid.NewGuid(), "Exam", 60, now.AddHours(2), now.AddHours(1), 1,
        "not-a-sha256", 1, true, true, null)]);
    Throws<InvalidDataException>(() => AvailableExamMapper.Map(invalid),
        "invalid assignment metadata is rejected");
}

static DeviceResponse Device(Guid id, Guid installationId) =>
    new(id, installationId, "Candidate PC", "Windows", "1.0.0", "active",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

static HttpResponseMessage Json<T>(HttpStatusCode status, T value) => new(status)
{
    Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        Encoding.UTF8, "application/json")
};

static AuthenticatedHttpClient Client(QueueHandler handler) =>
    new(new HttpClient(handler) { BaseAddress = new Uri("https://examifo.com/") }, new Tokens());

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}

static void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); } catch (T) { Console.WriteLine($"PASS: {name}"); return; }
    throw new InvalidOperationException($"FAIL: {name}");
}

static async Task ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); } catch (T) { Console.WriteLine($"PASS: {name}"); return; }
    throw new InvalidOperationException($"FAIL: {name}");
}

sealed class Tokens : IAuthenticatedTokenProvider
{
    public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("token");
    public Task<string> RefreshAfterUnauthorizedAsync(string rejectedAccessToken, CancellationToken cancellationToken = default) =>
        Task.FromResult("rotated");
}

sealed record Captured(string Method, string Path, string Body);

sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);
    public List<Captured> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Captured(request.Method.Method, request.RequestUri?.PathAndQuery ?? "", body));
        return _responses.Dequeue();
    }
}
