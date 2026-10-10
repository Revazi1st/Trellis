using Trellis.Application.Abstractions;
using Trellis.Application.Options;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Auth;

/// <summary>Mints an access+refresh pair and adds the refresh token's hash to the command's unit of work (committed with it). Shared by login and refresh.</summary>
internal static class TokenIssuer
{
    public static async Task<Result<AuthTokens>> IssueAsync(
        AuthenticatedUser user,
        ITokenProvider tokenProvider,
        IRefreshTokenStore refreshTokenStore,
        TimeProvider timeProvider,
        AuthTokenOptions options,
        CancellationToken cancellationToken)
    {
        string accessToken = tokenProvider.CreateAccessToken(user);
        string refreshToken = tokenProvider.CreateRefreshToken();

        DateTimeOffset expiresAt = timeProvider.GetUtcNow().AddDays(options.RefreshTokenDays);
        await refreshTokenStore.StoreAsync(
            user.Id, tokenProvider.HashRefreshToken(refreshToken), expiresAt, cancellationToken);

        return new AuthTokens(accessToken, refreshToken, options.AccessTokenMinutes * 60);
    }
}
