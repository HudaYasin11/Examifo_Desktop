using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Services;

namespace Examifo_Desktop.Infrastructure.Api.Clients;

public sealed class SubmissionApiClient(HttpClient httpClient, AuthenticationService authenticationService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<OfflineAuthorizationResponse> AuthorizeAsync(
        Guid examId, OfflineAuthorizationRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<OfflineAuthorizationRequest, OfflineAuthorizationResponse>(
            $"api/v1/exams/{examId}/offline-authorizations", request, cancellationToken);

    public Task<SyncPushResponse> PushAsync(
        SyncPushRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SyncPushRequest, SyncPushResponse>("api/v1/sync/push", request, cancellationToken);

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string route, TRequest value, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithRefreshAsync(route, value, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidDataException($"Examifo returned an empty response for {route}.");
    }

    private async Task<HttpResponseMessage> SendWithRefreshAsync<T>(
        string route, T value, CancellationToken cancellationToken)
    {
        string token = await authenticationService.GetValidAccessTokenAsync(cancellationToken);
        HttpResponseMessage response = await SendAsync(route, value, token, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        token = await authenticationService.RefreshAfterUnauthorizedAsync(token, cancellationToken);
        return await SendAsync(route, value, token, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync<T>(
        string route, T value, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(value, options: JsonOptions);
        return await httpClient.SendAsync(request, cancellationToken);
    }
}
