using XTemplate.Application.Abstractions;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Features.Auth.RevokeSessions;

internal sealed class RevokeSessionsCommandHandler(
    ICurrentUser currentUser,
    IRefreshTokenStore refreshTokenStore) : ICommandHandler<RevokeSessionsCommand>
{
    public async Task<Result> Handle(RevokeSessionsCommand command, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return Result.Failure(AuthErrors.InvalidCredentials);
        }

        await refreshTokenStore.RevokeAllAsync(userId, cancellationToken);
        return Result.Success();
    }
}
