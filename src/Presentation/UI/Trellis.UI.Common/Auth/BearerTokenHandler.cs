using System.Net.Http.Headers;

namespace Trellis.UI.Common.Auth;

/// <summary>Attaches a currently-valid access token to every outgoing API request (rotating it when expired).</summary>
internal sealed class BearerTokenHandler(IAccessTokenSource tokenSource) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (await tokenSource.GetValidAccessTokenAsync(cancellationToken) is { Length: > 0 } accessToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
