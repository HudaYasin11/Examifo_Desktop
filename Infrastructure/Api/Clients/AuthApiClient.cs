using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Examifo_Desktop.Infrastructure.Api.DTOs;
using Examifo_Desktop.Services;

namespace Examifo_Desktop.Infrastructure.Api.Clients;

public sealed class AuthApiClient(HttpClient httpClient) : ITokenRefreshClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/v1/auth/login", request, JsonOptions, cancellationToken);
        if (response.IsSuccessStatusCode)
            return await ReadAuthResponseAsync(response, "login", cancellationToken);

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
            return await ReadAuthResponseAsync(response, "refresh", cancellationToken);

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

    private static async Task<LoginResponse> ReadAuthResponseAsync(HttpResponseMessage response, string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            LoginResponse? result = await response.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions, cancellationToken);
            if (result is null || string.IsNullOrWhiteSpace(result.AccessToken)
                || string.IsNullOrWhiteSpace(result.RefreshToken) || result.DeviceId == Guid.Empty
                || result.User is null || result.User.Id == Guid.Empty || string.IsNullOrWhiteSpace(result.User.Name))
                throw new AuthApiException(HttpStatusCode.BadGateway, "INVALID_RESPONSE",
                    $"Examifo returned an invalid {operation} response.");
            return result;
        }
        catch (JsonException ex)
        {
            throw new AuthApiException(HttpStatusCode.BadGateway, "INVALID_RESPONSE",
                $"Examifo returned an invalid {operation} response.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new AuthApiException(HttpStatusCode.BadGateway, "INVALID_RESPONSE",
                $"Examifo returned an invalid {operation} response.", ex);
        }
    }
}

public sealed class AuthApiException(HttpStatusCode statusCode, string? code, string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}
