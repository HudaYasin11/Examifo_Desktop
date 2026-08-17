using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Infrastructure.Api.Clients;

public enum PackageDownloadResult { Downloaded, NotModified }

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

    public async Task<PackageDownloadResult> DownloadPackageAsync(string downloadUrl, Stream destination,
        long maximumBytes, string? localContentHash = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl.TrimStart('/'));
                if (!string.IsNullOrWhiteSpace(localContentHash))
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{localContentHash}\""));
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return PackageDownloadResult.NotModified;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long declaredLength
            && declaredLength > maximumBytes)
            throw new InvalidDataException("The exam package is larger than its validated manifest size.");
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException("The exam package exceeded its validated manifest size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0) throw new InvalidDataException("Examifo returned an empty exam package.");
        return PackageDownloadResult.Downloaded;
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
