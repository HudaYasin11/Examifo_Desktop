using System.Net.Http.Json;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Infrastructure.Api.Clients;

public sealed class SubmissionApiClient(AuthenticatedHttpClient authenticatedHttpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<OfflineAuthorizationResponse> AuthorizeAsync(
        Guid examId, OfflineAuthorizationRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<OfflineAuthorizationRequest, OfflineAuthorizationResponse>(
            $"api/v1/exams/{examId}/offline-authorizations", request, cancellationToken);

    public async Task<IReadOnlyList<OfflineAuthorizationSummary>> GetAuthorizationsAsync(
        Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("A device ID is required.", nameof(deviceId));
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get,
                $"api/v1/offline-authorizations?deviceId={deviceId:D}"),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<OfflineAuthorizationSummary>>(
            JsonOptions, cancellationToken) ?? throw new InvalidDataException("Examifo returned an empty authorization list.");
    }

    public async Task CancelAuthorizationAsync(
        Guid authorizationId, CancellationToken cancellationToken = default)
    {
        if (authorizationId == Guid.Empty)
            throw new ArgumentException("An authorization ID is required.", nameof(authorizationId));
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Delete,
                $"api/v1/offline-authorizations/{authorizationId:D}"),
            cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public Task<SyncPushResponse> PushAsync(
        SyncPushRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SyncPushRequest, SyncPushResponse>("api/v1/sync/push", request, cancellationToken);

    public async Task<SyncPullResponse> PullAsync(Guid clientId, long afterRevision,
        CancellationToken cancellationToken = default)
    {
        if (clientId == Guid.Empty) throw new ArgumentException("A client ID is required.", nameof(clientId));
        if (afterRevision < 0) throw new ArgumentOutOfRangeException(nameof(afterRevision));
        string route = $"api/v1/sync/pull?clientId={clientId:D}&afterRevision={afterRevision}";
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, route),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncPullResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Examifo returned an empty synchronization pull response.");
    }

    public async Task<SyncOperationStatusResponse> GetOperationStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("An operation ID is required.", nameof(operationId));
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"api/v1/sync/operations/{operationId:D}"),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SyncOperationStatusResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Examifo returned an empty synchronization status response.");
    }

    public async Task<AttemptRecoveryResponse> GetAttemptAsync(Guid attemptId,
        CancellationToken cancellationToken = default) =>
        await GetAsync<AttemptRecoveryResponse>($"api/v1/attempts/{Required(attemptId, nameof(attemptId)):D}",
            cancellationToken);

    public async Task<AttemptSyncSummaryResponse> GetAttemptSyncSummaryAsync(Guid attemptId,
        CancellationToken cancellationToken = default) =>
        await GetAsync<AttemptSyncSummaryResponse>(
            $"api/v1/attempts/{Required(attemptId, nameof(attemptId)):D}/sync-summary", cancellationToken);

    public async Task<AttemptResultResponse> GetAttemptResultAsync(Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        string route = $"api/v1/attempts/{Required(attemptId, nameof(attemptId)):D}/result";
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, route),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is not (System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Accepted))
            response.EnsureSuccessStatusCode();
        AttemptResultResponse result = await response.Content.ReadFromJsonAsync<AttemptResultResponse>(
            JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Examifo returned an empty attempt-result response.");
        string status = result.Status?.ToLowerInvariant()
            ?? throw new InvalidDataException("Examifo returned a result without a status.");
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted && status != "grading")
            throw new InvalidDataException("Examifo returned an inconsistent pending result response.");
        if (response.StatusCode == System.Net.HttpStatusCode.OK && status is not ("withheld" or "available"))
            throw new InvalidDataException("Examifo returned an unknown final result status.");
        return result;
    }

    private async Task<TResponse> GetAsync<TResponse>(string route, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, route),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidDataException($"Examifo returned an empty response for {route}.");
    }

    private static Guid Required(Guid value, string parameterName) => value == Guid.Empty
        ? throw new ArgumentException("A non-empty ID is required.", parameterName) : value;

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string route, TRequest value, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(value, options: JsonOptions)
            }, cancellationToken: cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ProblemDetailsResponse? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>(
                    JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Preserve the HTTP failure when the server did not return problem+json.
            }

            string message = problem?.Code switch
            {
                "PACKAGE_OUTDATED" => "The downloaded exam package is outdated. Refresh the exam while online.",
                "MAX_ATTEMPTS_REACHED" => "You have reached the maximum number of attempts for this exam.",
                "DEVICE_MISMATCH" => "This exam authorization belongs to a different registered device.",
                _ => problem?.Title ?? $"Examifo rejected offline access ({(int)response.StatusCode} {response.ReasonPhrase})."
            };
            throw new AuthApiException(response.StatusCode, problem?.Code, message);
        }
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidDataException($"Examifo returned an empty response for {route}.");
    }

}
