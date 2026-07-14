using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using XTemplate.Application.Abstractions;
using XTemplate.Application.Authorization;
using XTemplate.Application.Options;

namespace XTemplate.Infrastructure.Authentication;

/// <summary>
/// Mints HMAC-SHA256 access tokens carrying the user's permissions as "permission" claims, plus
/// opaque, high-entropy refresh tokens (only their SHA-256 hash is ever persisted).
/// </summary>
internal sealed class JwtTokenProvider(TimeProvider timeProvider, AuthTokenOptions options) : ITokenProvider
{
    private readonly SigningCredentials _signingCredentials = new(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
        SecurityAlgorithms.HmacSha256);

    public string CreateAccessToken(AuthenticatedUser user)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Name, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            .. user.Permissions.Select(permission => new Claim(AuthClaims.Permission, permission)),
        ];

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(options.AccessTokenMinutes),
            SigningCredentials = _signingCredentials,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public string CreateRefreshToken()
        => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public string HashRefreshToken(string refreshToken)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}
