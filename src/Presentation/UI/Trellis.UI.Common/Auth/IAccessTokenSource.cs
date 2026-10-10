namespace Trellis.UI.Common.Auth;

/// <summary>
/// Supplies a currently-valid access token for outgoing API calls, transparently rotating an expired
/// one through the refresh endpoint. Null means the session is over and the user must sign in again.
/// While the refresh endpoint is failing transiently (429, 5xx, unreachable), the stored token comes
/// back instead of null: a session ends only when its refresh token is rejected.
/// </summary>
public interface IAccessTokenSource
{
    ValueTask<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken = default);
}
