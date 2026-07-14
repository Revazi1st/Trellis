namespace XTemplate.UI.Common.Services;

/// <summary>
/// Platform seam for holding the current tokens. UI.Web backs this per-circuit in memory;
/// UI.Mobile (deferred) will back it with SecureStorage. Kept minimal and synchronous-friendly.
/// </summary>
public interface ITokenStore
{
    ValueTask<string?> GetAccessTokenAsync();

    ValueTask<string?> GetRefreshTokenAsync();

    ValueTask SetTokensAsync(string accessToken, string refreshToken);

    ValueTask ClearAsync();
}
