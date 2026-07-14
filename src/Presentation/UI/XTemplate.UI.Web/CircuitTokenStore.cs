using XTemplate.UI.Common.Services;

namespace XTemplate.UI.Web;

/// <summary>
/// Per-circuit, in-memory token store for Blazor Server. Demo-grade: tokens live in the user's
/// server-side circuit (ADR-0007 flags BFF/cookies for production). Scoped == one per circuit.
/// </summary>
internal sealed class CircuitTokenStore : ITokenStore
{
    private string? _accessToken;
    private string? _refreshToken;

    public ValueTask<string?> GetAccessTokenAsync() => ValueTask.FromResult(_accessToken);

    public ValueTask<string?> GetRefreshTokenAsync() => ValueTask.FromResult(_refreshToken);

    public ValueTask SetTokensAsync(string accessToken, string refreshToken)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync()
    {
        _accessToken = null;
        _refreshToken = null;
        return ValueTask.CompletedTask;
    }
}
