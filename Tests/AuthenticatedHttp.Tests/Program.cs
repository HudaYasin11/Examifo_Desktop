using System.Net;
using System.Text;
using Examifo_Desktop.Infrastructure.Api;

await TestAddsBearerWithoutRefreshAsync();
await TestRefreshesOnceAndRecreatesPostAsync();
await TestDoesNotLoopOnSecondUnauthorizedAsync();
await TestRejectsCrossOriginTokenLeakAsync();
await TestRejectsInsecureTransportAsync();
Console.WriteLine("All authenticated HTTP tests passed.");

static async Task TestAddsBearerWithoutRefreshAsync()
{
    var tokens = new FakeTokenProvider();
    var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
    var client = CreateClient(handler, tokens);
    using HttpResponseMessage response = await client.SendAsync(
        () => new HttpRequestMessage(HttpMethod.Get, "api/v1/exams/available"));
    Assert(response.StatusCode == HttpStatusCode.OK, "successful response is returned");
    Assert(handler.AuthorizationTokens.SequenceEqual(["access-1"]), "current bearer token is attached");
    Assert(tokens.RefreshCalls == 0, "successful request does not refresh");
}

static async Task TestRefreshesOnceAndRecreatesPostAsync()
{
    var tokens = new FakeTokenProvider();
    int requestFactoryCalls = 0;
    var handler = new RecordingHandler((request, call) =>
        new HttpResponseMessage(call == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK));
    var client = CreateClient(handler, tokens);
    using HttpResponseMessage response = await client.SendAsync(() =>
    {
        requestFactoryCalls++;
        return new HttpRequestMessage(HttpMethod.Post, "api/v1/sync/push")
        {
            Content = new StringContent("{\"batchId\":\"stable\"}", Encoding.UTF8, "application/json")
        };
    });
    Assert(response.StatusCode == HttpStatusCode.OK, "retry response is returned");
    Assert(tokens.RefreshCalls == 1, "401 triggers one token rotation");
    Assert(requestFactoryCalls == 2, "request is safely recreated for retry");
    Assert(handler.AuthorizationTokens.SequenceEqual(["access-1", "access-2"]), "retry uses rotated token");
    Assert(handler.Bodies.Distinct().Single() == "{\"batchId\":\"stable\"}", "POST body survives retry unchanged");
}

static async Task TestDoesNotLoopOnSecondUnauthorizedAsync()
{
    var tokens = new FakeTokenProvider();
    var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
    var client = CreateClient(handler, tokens);
    using HttpResponseMessage response = await client.SendAsync(
        () => new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/me"));
    Assert(response.StatusCode == HttpStatusCode.Unauthorized, "second 401 is returned to caller");
    Assert(handler.Calls == 2 && tokens.RefreshCalls == 1, "401 retry is bounded to one attempt");
}

static async Task TestRejectsCrossOriginTokenLeakAsync()
{
    var tokens = new FakeTokenProvider();
    var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
    var client = CreateClient(handler, tokens);
    await AssertThrowsAsync<InvalidOperationException>(() => client.SendAsync(
        () => new HttpRequestMessage(HttpMethod.Get, "https://attacker.example/package")),
        "cross-origin authenticated request is rejected");
    Assert(handler.Calls == 0, "bearer token is never sent to another origin");
}

static async Task TestRejectsInsecureTransportAsync()
{
    var tokens = new FakeTokenProvider();
    var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
    var client = new AuthenticatedHttpClient(
        new HttpClient(handler) { BaseAddress = new Uri("http://examifo.example/") }, tokens);
    await AssertThrowsAsync<InvalidOperationException>(() => client.SendAsync(
        () => new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/me")),
        "authenticated traffic rejects non-HTTPS transport");
    Assert(handler.Calls == 0, "token is not transmitted over insecure HTTP");
}

static AuthenticatedHttpClient CreateClient(RecordingHandler handler, FakeTokenProvider tokens) =>
    new(new HttpClient(handler) { BaseAddress = new Uri("https://examifo.com/") }, tokens);

static void Assert(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}

static async Task AssertThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { Console.WriteLine($"PASS: {name}"); return; }
    throw new InvalidOperationException($"FAIL: {name}");
}

sealed class FakeTokenProvider : IAuthenticatedTokenProvider
{
    public int RefreshCalls { get; private set; }
    public Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("access-1");
    public Task<string> RefreshAfterUnauthorizedAsync(string rejectedAccessToken,
        CancellationToken cancellationToken = default)
    {
        if (rejectedAccessToken != "access-1")
            throw new InvalidOperationException("FAIL: refresh receives rejected token");
        Console.WriteLine("PASS: refresh receives rejected token");
        RefreshCalls++;
        return Task.FromResult("access-2");
    }
}

sealed class RecordingHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public List<string?> AuthorizationTokens { get; } = [];
    public List<string> Bodies { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        AuthorizationTokens.Add(request.Headers.Authorization?.Parameter);
        if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        return responseFactory(request, Calls);
    }
}
