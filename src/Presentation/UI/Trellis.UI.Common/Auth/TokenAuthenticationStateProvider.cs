using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Trellis.Api.Contracts.Auth;
using Trellis.UI.Common.Services;

namespace Trellis.UI.Common.Auth;

/// <summary>
/// Derives the auth state from the stored access token's claims (including permission claims, so
/// AuthorizeView can gate on them), rotating an expired access token through the refresh endpoint so
/// the session outlives the short access-token lifetime. Demo-grade for Blazor Server per-circuit
/// tokens (ADR-0007).
/// </summary>
public sealed class TokenAuthenticationStateProvider(ITokenStore tokenStore, IAuthApiClient authApi)
    : AuthenticationStateProvider, IAccessTokenSource, IDisposable
{
    private const string AuthenticationType = "jwt";

    /// <summary>Rotate slightly before actual expiry so an in-flight request never carries a just-died token.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        string? accessToken = await GetValidAccessTokenAsync();

        ClaimsIdentity identity = string.IsNullOrEmpty(accessToken)
            ? new ClaimsIdentity()
            : new ClaimsIdentity(JwtClaims.Read(accessToken), AuthenticationType, ClaimTypes.Name, "permission");

        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public async ValueTask<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        string? accessToken = await tokenStore.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        if (!IsExpired(accessToken))
        {
            return accessToken;
        }

        // Refresh tokens are single-use, so concurrent callers must not race to rotate: the winner
        // refreshes, the rest re-read the rotated pair after the lock.
        bool sessionEnded = false;
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            accessToken = await tokenStore.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(accessToken))
            {
                return null;
            }

            if (!IsExpired(accessToken))
            {
                return accessToken;
            }

            string? refreshToken = await tokenStore.GetRefreshTokenAsync();
            ApiResult<TokenResponse>? refreshed = string.IsNullOrEmpty(refreshToken)
                ? null
                : await authApi.RefreshAsync(new RefreshRequest(refreshToken), cancellationToken);

            if (refreshed is not { IsSuccess: true, Value: not null })
            {
                await tokenStore.ClearAsync();
                sessionEnded = true;
                return null;
            }

            await tokenStore.SetTokensAsync(refreshed.Value.AccessToken, refreshed.Value.RefreshToken);
            return refreshed.Value.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
            if (sessionEnded)
            {
                NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
            }
        }
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

    private static bool IsExpired(string accessToken)
        => JwtClaims.GetExpiration(accessToken) is not { } expiresAt
           || expiresAt <= DateTimeOffset.UtcNow + ExpirySkew;

    public void Dispose() => _refreshLock.Dispose();
}
