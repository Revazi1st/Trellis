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
        RefreshToken? token = await context.Set<RefreshToken>()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        if (token is null || token.ConsumedAtUtc is not null || token.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return Result.Failure<Guid>(Error.Unauthorized("Auth.InvalidRefreshToken", "The refresh token is invalid or has expired."));
        }

        token.ConsumedAtUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken);

        return token.UserId;
    }
}
