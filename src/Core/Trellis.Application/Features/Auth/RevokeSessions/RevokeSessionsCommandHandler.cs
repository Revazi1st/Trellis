using Trellis.Application.Abstractions;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Auth.RevokeSessions;

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
