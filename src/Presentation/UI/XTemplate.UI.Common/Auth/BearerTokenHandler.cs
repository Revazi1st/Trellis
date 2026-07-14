using System.Net.Http.Headers;
using XTemplate.UI.Common.Services;

namespace XTemplate.UI.Common.Auth;

/// <summary>Attaches the stored access token to every outgoing API request.</summary>
internal sealed class BearerTokenHandler(ITokenStore tokenStore) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (await tokenStore.GetAccessTokenAsync() is { Length: > 0 } accessToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
