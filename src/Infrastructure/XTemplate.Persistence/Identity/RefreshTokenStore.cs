using Microsoft.EntityFrameworkCore;
using XTemplate.Application.Abstractions;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Persistence.Identity;

internal sealed class RefreshTokenStore(AppDbContext context, TimeProvider timeProvider) : IRefreshTokenStore
{
    public async Task StoreAsync(Guid userId, string tokenHash, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
    {
        context.Set<RefreshToken>().Add(new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAtUtc = expiresAtUtc,
        });

        await context.SaveChangesAsync(cancellationToken);
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
            return Result.Failure<Guid>(Error.Unauthorized("Auth.InvalidRefreshToken", "The refresh token is invalid or has expired."));
        }

        return await context.Set<RefreshToken>()
            .Where(t => t.TokenHash == tokenHash)
            .Select(t => t.UserId)
            .FirstAsync(cancellationToken);
    }
}
