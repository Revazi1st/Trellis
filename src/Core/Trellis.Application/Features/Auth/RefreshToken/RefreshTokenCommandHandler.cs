using Trellis.Application.Abstractions;
using Trellis.Application.Options;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Auth.RefreshToken;

internal sealed class RefreshTokenCommandHandler(
    IIdentityService identityService,
    ITokenProvider tokenProvider,
    IRefreshTokenStore refreshTokenStore,
    TimeProvider timeProvider,
    AuthTokenOptions options) : ICommandHandler<RefreshTokenCommand, AuthTokens>
{
    public async Task<Result<AuthTokens>> Handle(RefreshTokenCommand command, CancellationToken cancellationToken)
    {
        string tokenHash = tokenProvider.HashRefreshToken(command.RefreshToken);

        // Single-use rotation: consuming invalidates the presented token before a new pair is minted.
        Result<Guid> userId = await refreshTokenStore.ConsumeAsync(tokenHash, cancellationToken);
        if (userId.IsFailure)
        {
            return Result.Failure<AuthTokens>(AuthErrors.InvalidRefreshToken);
        }

        Result<AuthenticatedUser> user = await identityService.GetUserAsync(userId.Value, cancellationToken);
        if (user.IsFailure)
        {
            return Result.Failure<AuthTokens>(AuthErrors.InvalidRefreshToken);
        }

        return await TokenIssuer.IssueAsync(
            user.Value, tokenProvider, refreshTokenStore, timeProvider, options, cancellationToken);
    }
}
