using System.Net;
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
/// <para>
/// Only a 401 from the refresh endpoint ends the session: the refresh token itself was rejected. A
/// 429, a 5xx or no response at all says nothing about the session, so the stored pair is kept, the
/// next attempt waits out a short hold-off, and callers get the stored token meanwhile (a protected
/// page does not bounce to sign-in; at worst one API call answers 401 and the next one retries).
/// </para>
/// </summary>
/// <param name="tokenStore">Host-provided token storage.</param>
/// <param name="authApi">Client for the refresh and logout endpoints.</param>
/// <param name="time">Clock for expiry and the hold-off; the system clock unless the host registers one.</param>
public sealed class TokenAuthenticationStateProvider(
    ITokenStore tokenStore,
    IAuthApiClient authApi,
    TimeProvider time)
    : AuthenticationStateProvider, IAccessTokenSource, IDisposable
{
    private const string AuthenticationType = "jwt";

    /// <summary>Rotate slightly before actual expiry so an in-flight request never carries a just-died token.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// After a transient refresh failure, no new attempt for this long: callers in the window get the
    /// stored token instead of queueing on the lock to ask an API that has just said "not now".
    /// </summary>
    private static readonly TimeSpan RefreshHoldOff = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>UTC ticks before which no refresh is attempted; 0 when no hold-off is active.</summary>
    private long _refreshHoldOffUntilTicks;

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

        if (!IsExpired(accessToken) || IsHoldingOff())
        {
            return accessToken;
        }

        // Refresh tokens are single-use, so concurrent callers must not race to rotate: the winner
        // refreshes, the rest re-read the rotated pair (or the winner's hold-off) after the lock.
        (string? AccessToken, bool SessionEnded) outcome;
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            outcome = await RefreshUnderLockAsync(cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }

        if (outcome.SessionEnded)
        {
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }

        return outcome.AccessToken;
    }

    public async Task SignInAsync(string accessToken, string refreshToken)
    {
        ClearHoldOff();
        await tokenStore.SetTokensAsync(accessToken, refreshToken);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }

    /// <summary>
    /// Signs the circuit out. The stored pair is forgotten and the change announced at once (a protected
    /// page sends the user to sign-in); then the refresh token is revoked at the API
    /// (<c>POST /auth/logout</c>), so a copy of it is worthless. The revoke is best-effort: when the API
    /// cannot be reached the token simply runs out (<c>RefreshTokenDays</c>), and the user is signed out
    /// locally either way.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        string? refreshToken = await ForgetTokensAsync(cancellationToken);
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        await RevokeAsync(refreshToken, cancellationToken);
    }

    /// <summary>
    /// Revokes and forgets the session without announcing a change: for a host whose UI goes away
    /// together with its tokens (UI.Web: the circuit closed). Best-effort, like <see cref="SignOutAsync"/>.
    /// </summary>
    public async Task CloseSessionAsync(CancellationToken cancellationToken = default)
    {
        string? refreshToken = await ForgetTokensAsync(cancellationToken);
        await RevokeAsync(refreshToken, cancellationToken);
    }

    /// <summary>
    /// Clears the stored pair and returns the refresh token it held. Under the refresh lock: a rotation in
    /// flight finishes first, so the token revoked is the current one (the consumed one is already dead)
    /// and the rotation cannot store a fresh pair after the sign-out.
    /// </summary>
    private async Task<string?> ForgetTokensAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            ClearHoldOff();
            string? refreshToken = await tokenStore.GetRefreshTokenAsync();
            await tokenStore.ClearAsync();
            return refreshToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RevokeAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        // The outcome is not needed: a failure means the token outlives the session until it expires,
        // and the API client has already turned an unreachable API into a result.
        _ = await authApi.LogoutAsync(new LogoutRequest(refreshToken), cancellationToken);
    }

    /// <summary>The body of a refresh; the caller holds <see cref="_refreshLock"/>.</summary>
    private async Task<(string? AccessToken, bool SessionEnded)> RefreshUnderLockAsync(CancellationToken cancellationToken)
    {
        string? accessToken = await tokenStore.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(accessToken))
        {
            return (null, false);
        }

        if (!IsExpired(accessToken) || IsHoldingOff())
        {
            return (accessToken, false);
        }

        string? refreshToken = await tokenStore.GetRefreshTokenAsync();
        if (string.IsNullOrEmpty(refreshToken))
        {
            await tokenStore.ClearAsync();
            return (null, true);
        }

        ApiResult<TokenResponse> refreshed = await authApi.RefreshAsync(new RefreshRequest(refreshToken), cancellationToken);

        if (refreshed is { IsSuccess: true, Value: { } tokens })
        {
            ClearHoldOff();
            await tokenStore.SetTokensAsync(tokens.AccessToken, tokens.RefreshToken);
            return (tokens.AccessToken, false);
        }

        if (refreshed.StatusCode == (int)HttpStatusCode.Unauthorized)
        {
            // The refresh token itself was rejected (expired, revoked, replayed): the session is over.
            await tokenStore.ClearAsync();
            return (null, true);
        }

        // Transient (429, 5xx, no response): a failed refresh is not a sign-out.
        HoldOffRefresh();
        return (accessToken, false);
    }

    private bool IsExpired(string accessToken)
        => JwtClaims.GetExpiration(accessToken) is not { } expiresAt
           || expiresAt <= time.GetUtcNow() + ExpirySkew;

    private bool IsHoldingOff() => time.GetUtcNow().UtcTicks < Volatile.Read(ref _refreshHoldOffUntilTicks);

    private void HoldOffRefresh() => Volatile.Write(ref _refreshHoldOffUntilTicks, (time.GetUtcNow() + RefreshHoldOff).UtcTicks);

    private void ClearHoldOff() => Volatile.Write(ref _refreshHoldOffUntilTicks, 0);

    public void Dispose() => _refreshLock.Dispose();
}
