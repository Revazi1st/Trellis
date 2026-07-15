using Microsoft.EntityFrameworkCore;
using XTemplate.Application.Abstractions;

namespace XTemplate.Persistence.Idempotency;

/// <summary>
/// Reserves keys via an INSERT against the primary key — a concurrent duplicate loses the race and
/// gets a DbUpdateException, which we translate to "already reserved".
/// </summary>
internal sealed class IdempotencyStore(AppDbContext context, TimeProvider timeProvider) : IIdempotencyStore
{
    public async Task<bool> TryReserveAsync(string key, CancellationToken cancellationToken)
    {
        context.Set<IdempotencyKey>().Add(new IdempotencyKey { Key = key, CreatedAtUtc = timeProvider.GetUtcNow() });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        await context.Set<IdempotencyKey>()
            .Where(k => k.Key == key)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
