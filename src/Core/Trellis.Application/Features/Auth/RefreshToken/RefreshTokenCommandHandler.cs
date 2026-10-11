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
        // Every failure before minting (an unknown, expired or consumed token, a user deleted since) is
        // the same opaque Auth.InvalidRefreshToken; MapOnFailure sits before the issuing step so a
        // failure while minting keeps its own error.
        return await refreshTokenStore.ConsumeAsync(tokenHash, cancellationToken)
            .Bind(userId => identityService.GetUserAsync(userId, cancellationToken))
            .MapOnFailure(static _ => AuthErrors.InvalidRefreshToken)
            .Bind(user => TokenIssuer.IssueAsync(
                user, tokenProvider, refreshTokenStore, timeProvider, options, cancellationToken));
    }
}
