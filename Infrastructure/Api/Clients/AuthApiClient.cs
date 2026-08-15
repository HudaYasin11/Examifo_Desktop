using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.DTOs;

namespace Examifo_Desktop.Infrastructure.Api.Clients;

public sealed class AuthApiClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/v1/auth/login", request, JsonOptions, cancellationToken);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, cancellationToken)
                ?? throw new AuthApiException(HttpStatusCode.BadGateway, "INVALID_RESPONSE", "Examifo returned an empty login response.");

        ProblemDetailsResponse? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            // The server did not return an application/problem+json payload.
        }

        throw new AuthApiException(response.StatusCode, problem?.Code, problem?.Title ?? "Unable to sign in to Examifo.");
    }

    public async Task<LoginResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            "api/v1/auth/refresh", request, JsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, cancellationToken)
                ?? throw new AuthApiException(HttpStatusCode.BadGateway, "INVALID_RESPONSE", "Examifo returned an empty refresh response.");

        ProblemDetailsResponse? problem = null;
        try
        {
            problem = await response.Content.ReadFromJsonAsync<ProblemDetailsResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
        }

        throw new AuthApiException(response.StatusCode, problem?.Code,
            problem?.Title ?? "Your Examifo session could not be refreshed.");
    }
}

public sealed class AuthApiException(HttpStatusCode statusCode, string? code, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}
