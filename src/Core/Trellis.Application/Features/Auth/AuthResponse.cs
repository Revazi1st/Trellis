namespace Trellis.Application.Features.Auth;

/// <summary>The token pair returned by login and refresh.</summary>
public sealed record AuthTokens(string AccessToken, string RefreshToken, int ExpiresInSeconds);
