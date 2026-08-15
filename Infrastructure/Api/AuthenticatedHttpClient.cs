using System.Net;
using System.Net.Http.Headers;

namespace Examifo_Desktop.Infrastructure.Api;

public interface IAuthenticatedTokenProvider
{
    Task<string> GetValidAccessTokenAsync(CancellationToken cancellationToken = default);
    Task<string> RefreshAfterUnauthorizedAsync(
        string rejectedAccessToken,
        CancellationToken cancellationToken = default);
}

public sealed class AuthenticatedHttpClient(
    HttpClient httpClient,
    IAuthenticatedTokenProvider tokenProvider)
{
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);

        string token = await tokenProvider.GetValidAccessTokenAsync(cancellationToken);
        HttpResponseMessage response = await SendOnceAsync(
            requestFactory, token, completionOption, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        token = await tokenProvider.RefreshAfterUnauthorizedAsync(token, cancellationToken);
        return await SendOnceAsync(requestFactory, token, completionOption, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Func<HttpRequestMessage> requestFactory,
        string token,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = requestFactory()
            ?? throw new InvalidOperationException("The authenticated request factory returned null.");
        ValidateDestination(request.RequestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await httpClient.SendAsync(request, completionOption, cancellationToken);
    }

    private void ValidateDestination(Uri? requestUri)
    {
        if (requestUri is null)
            throw new InvalidOperationException("Authenticated requests require a request URI.");
        if (!requestUri.IsAbsoluteUri)
        {
            if (httpClient.BaseAddress?.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Authenticated Examifo requests require HTTPS.");
            return;
        }

        Uri? baseAddress = httpClient.BaseAddress;
        if (baseAddress is null
            || baseAddress.Scheme != Uri.UriSchemeHttps
            || !string.Equals(requestUri.Scheme, baseAddress.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(requestUri.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase)
            || requestUri.Port != baseAddress.Port)
            throw new InvalidOperationException("Refusing to send an Examifo bearer token to another origin.");
    }
}
