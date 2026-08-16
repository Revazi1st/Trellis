namespace Trellis.UI.Common.Auth;

/// <summary>
/// Supplies a currently-valid access token for outgoing API calls, transparently rotating an expired
/// one through the refresh endpoint. Null means the session is over and the user must sign in again.
/// </summary>
public interface IAccessTokenSource
{
    ValueTask<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken = default);
}
