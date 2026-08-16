using XTemplate.Application.Abstractions;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Features.Auth.Logout;

internal sealed class LogoutCommandHandler(
    ITokenProvider tokenProvider,
    IRefreshTokenStore refreshTokenStore) : ICommandHandler<LogoutCommand>
{
    public async Task<Result> Handle(LogoutCommand command, CancellationToken cancellationToken)
    {
        // Revoke (not consume): logout is idempotent, never leaks whether the token was still live,
        // and a double-click must not trip the stolen-token replay detection.
        // The access token stays stateless and simply expires (short lifetime by design, ADR-0007).
        await refreshTokenStore.RevokeAsync(tokenProvider.HashRefreshToken(command.RefreshToken), cancellationToken);
        return Result.Success();
    }
}
