using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using XTemplate.UI.Common.Services;

namespace XTemplate.UI.Common.Auth;

/// <summary>
/// Derives the auth state from the stored access token's claims (including permission claims, so
/// AuthorizeView can gate on them). Demo-grade for Blazor Server per-circuit tokens (ADR-0007).
/// </summary>
public sealed class TokenAuthenticationStateProvider(ITokenStore tokenStore) : AuthenticationStateProvider
{
    private const string AuthenticationType = "jwt";

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        string? accessToken = await tokenStore.GetAccessTokenAsync();

        ClaimsIdentity identity = string.IsNullOrEmpty(accessToken)
            ? new ClaimsIdentity()
            : new ClaimsIdentity(JwtClaims.Read(accessToken), AuthenticationType, ClaimTypes.Name, "permission");

        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public async Task SignInAsync(string accessToken, string refreshToken)
    {
        await tokenStore.SetTokensAsync(accessToken, refreshToken);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    public async Task SignOutAsync()
    {
        await tokenStore.ClearAsync();
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
