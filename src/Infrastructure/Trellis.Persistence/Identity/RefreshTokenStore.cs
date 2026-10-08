using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Trellis.Application.Abstractions;
using Trellis.SharedKernel.Results;

namespace Trellis.Persistence.Identity;

internal sealed class RefreshTokenStore(
    AppDbContext context,
    TimeProvider timeProvider,
    ILogger<RefreshTokenStore> logger) : IRefreshTokenStore
{
    public Task StoreAsync(Guid userId, string tokenHash, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
    {
        // Tracked only: ports never commit. The command's UnitOfWork decorator saves it together with
        // everything else the command did, and a command that fails afterwards leaves no orphan token.
        context.Set<RefreshToken>().Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAtUtc = expiresAtUtc,
        });

        return Task.CompletedTask;
    }

    public async Task<Result<Guid>> ConsumeAsync(string tokenHash, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        // Single conditional UPDATE, not read-check-write: of two concurrent presentations of the same
        // token, exactly one can flip ConsumedAtUtc from NULL — the other sees zero rows and is rejected.
        int consumed = await context.Set<RefreshToken>()
            .Where(t => t.TokenHash == tokenHash && t.ConsumedAtUtc == null && t.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.ConsumedAtUtc, now), cancellationToken);

        if (consumed == 0)
        {
            // A replay of an already-consumed token is the classic stolen-token signal (OAuth
            // security BCP): kill the whole session family, not just this request.
            Guid? replayedUser = await context.Set<RefreshToken>()
                .Where(t => t.TokenHash == tokenHash && t.ConsumedAtUtc != null)
                .Select(t => (Guid?)t.UserId)
                .FirstOrDefaultAsync(cancellationToken);

            if (replayedUser is { } compromisedUserId)
            {
                int revoked = await RevokeAllAsync(compromisedUserId, cancellationToken);
                logger.LogWarning(
                    "Refresh-token replay detected for user {UserId}; revoked {RevokedCount} outstanding sessions",
                    compromisedUserId, revoked);
            }

            return Result.Failure<Guid>(Error.Unauthorized("Auth.InvalidRefreshToken", "The refresh token is invalid or has expired."));
        }

        return await context.Set<RefreshToken>()
            .Where(t => t.TokenHash == tokenHash)
            .Select(t => t.UserId)
            .FirstAsync(cancellationToken);
    }

    public async Task RevokeAsync(string tokenHash, CancellationToken cancellationToken)
        => await context.Set<RefreshToken>()
            .Where(t => t.TokenHash == tokenHash && t.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.ConsumedAtUtc, timeProvider.GetUtcNow()),
                cancellationToken);

    public async Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken)
        => await context.Set<RefreshToken>()
            .Where(t => t.UserId == userId && t.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(t => t.ConsumedAtUtc, timeProvider.GetUtcNow()),
                cancellationToken);
}
