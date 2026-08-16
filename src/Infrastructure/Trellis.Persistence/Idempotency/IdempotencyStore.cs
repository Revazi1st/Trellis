using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trellis.Application.Abstractions;

namespace Trellis.Persistence.Idempotency;

/// <summary>
/// Reserves keys via an INSERT against the primary key — a concurrent duplicate loses the race and
/// gets a unique violation, which we translate to "already reserved". Only that specific violation
/// means duplicate: any other database failure propagates, so a transient error is never
/// misreported to the caller as a 409.
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
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
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
