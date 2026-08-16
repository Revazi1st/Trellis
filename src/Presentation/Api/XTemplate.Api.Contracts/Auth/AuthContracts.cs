namespace XTemplate.Api.Contracts.Auth;

public sealed record RegisterRequest(string Email, string Password);

public sealed record RegisterResponse(Guid UserId);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record TokenResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds);
