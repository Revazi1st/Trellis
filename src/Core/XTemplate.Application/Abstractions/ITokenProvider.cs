namespace XTemplate.Application.Abstractions;

/// <summary>Mints signed access tokens and opaque refresh tokens. Pure token concerns — implemented in Infrastructure.</summary>
public interface ITokenProvider
{
    string CreateAccessToken(AuthenticatedUser user);

    /// <summary>A high-entropy opaque refresh token; only its hash is persisted.</summary>
    string CreateRefreshToken();

    string HashRefreshToken(string refreshToken);
}
