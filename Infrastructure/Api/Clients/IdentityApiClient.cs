using System.Net.Http.Json;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Infrastructure.Api.Clients;

public interface ICurrentIdentityClient
{
    Task<CurrentIdentityResponse> GetCurrentAsync(CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
}

public sealed class IdentityApiClient(AuthenticatedHttpClient authenticatedHttpClient) : ICurrentIdentityClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CurrentIdentityResponse> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "api/v1/auth/me"),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new AuthApiException(response.StatusCode, "SESSION_REJECTED", "The saved Examifo session is no longer valid.");
        response.EnsureSuccessStatusCode();
        CurrentIdentityResponse result = await response.Content.ReadFromJsonAsync<CurrentIdentityResponse>(
            JsonOptions, cancellationToken) ?? throw new InvalidDataException("Examifo returned an empty identity response.");
        if (result.User is null || result.User.Id == Guid.Empty || string.IsNullOrWhiteSpace(result.User.Name)
            || result.DeviceId == Guid.Empty || result.ServerTimeUtc == default)
            throw new InvalidDataException("Examifo returned an invalid identity response.");
        return result;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await authenticatedHttpClient.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/logout"),
            cancellationToken: cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
