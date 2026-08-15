using System.Net.Http.Json;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Infrastructure.Api.Clients;

public sealed class ExamApiClient(AuthenticatedHttpClient authenticatedHttpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<AvailableExamsResponse> GetAvailableExamsAsync(
        DateTimeOffset? modifiedSinceUtc = null,
        CancellationToken cancellationToken = default)
    {
        string route = "api/v1/exams/available";
        if (modifiedSinceUtc is not null)
            route += $"?modifiedSinceUtc={Uri.EscapeDataString(modifiedSinceUtc.Value.UtcDateTime.ToString("O"))}";
        return GetAsync<AvailableExamsResponse>(route, cancellationToken);
    }

    public Task<ExamMetadataResponse> GetExamAsync(Guid examId, CancellationToken cancellationToken = default) =>
        GetAsync<ExamMetadataResponse>($"api/v1/exams/{examId}", cancellationToken);

    public Task<PackageManifestResponse> GetManifestAsync(Guid examId, CancellationToken cancellationToken = default) =>
        GetAsync<PackageManifestResponse>($"api/v1/exams/{examId}/package/manifest", cancellationToken);

    public async Task<byte[]> DownloadPackageAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, downloadUrl.TrimStart('/')),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task<T> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, route),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               ?? throw new InvalidDataException($"Examifo returned an empty response for {route}.");
    }

}
