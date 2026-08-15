using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Services;

namespace Examifo_Desktop.Infrastructure.Api.Clients;

public sealed class ExamApiClient(HttpClient httpClient, AuthenticationService authenticationService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<AvailableExamsResponse> GetAvailableExamsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AvailableExamsResponse>("api/v1/exams/available", cancellationToken);

    public Task<ExamMetadataResponse> GetExamAsync(Guid examId, CancellationToken cancellationToken = default) =>
        GetAsync<ExamMetadataResponse>($"api/v1/exams/{examId}", cancellationToken);

    public Task<PackageManifestResponse> GetManifestAsync(Guid examId, CancellationToken cancellationToken = default) =>
        GetAsync<PackageManifestResponse>($"api/v1/exams/{examId}/package/manifest", cancellationToken);

    public async Task<byte[]> DownloadPackageAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await SendWithRefreshAsync(
            () => new HttpRequestMessage(HttpMethod.Get, downloadUrl.TrimStart('/')), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<T> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendWithRefreshAsync(
            () => new HttpRequestMessage(HttpMethod.Get, route), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               ?? throw new InvalidDataException($"Examifo returned an empty response for {route}.");
    }

    private async Task<HttpResponseMessage> SendWithRefreshAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        string token = await authenticationService.GetValidAccessTokenAsync(cancellationToken);
        HttpResponseMessage response = await SendAsync(requestFactory, token, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        token = await authenticationService.RefreshAfterUnauthorizedAsync(token, cancellationToken);
        return await SendAsync(requestFactory, token, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        string token,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }
}
