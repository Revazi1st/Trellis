using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Trellis.Application.Abstractions;

namespace Trellis.Persistence.Idempotency;

/// <summary>
/// The reservation rides the command's unit of work: <see cref="Reserve"/> only tracks the row on the
/// scoped <see cref="AppDbContext"/>, and the UnitOfWork decorator's commit inserts it in the same
/// transaction as the aggregate and its outbox rows. A duplicate therefore loses at the primary key,
/// inside <see cref="AppDbContext.CommitAsync"/>, which reports it as <see cref="IdempotencyErrors.Duplicate"/>;
/// a failed command never inserts anything. <see cref="CompleteAsync"/> is the one write of its own,
/// after the response went out.
/// </summary>
internal sealed class IdempotencyStore(AppDbContext context, TimeProvider timeProvider) : IIdempotencyStore
{
    public async Task<IdempotencyLookup> FindAsync(string key, CancellationToken cancellationToken)
    {
        // Untracked on purpose: the row found here is never written through this lookup, and a tracked
        // copy would collide with the reservation this same scope is about to add.
        IdempotencyKey? row = await context.Set<IdempotencyKey>()
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key, cancellationToken);

        return row switch
        {
            null => IdempotencyLookup.None,
            { IsCompleted: false } => new IdempotencyLookup.InFlight(row.CreatedAtUtc),
            _ => new IdempotencyLookup.Completed(row.RequestFingerprint, row.ToStoredResponse()),
        };
    }

    public void Reserve(string key, string requestFingerprint)
        => context.Set<IdempotencyKey>().Add(new IdempotencyKey
        {
            Key = key,
            RequestFingerprint = requestFingerprint,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        });

    public async Task CompleteAsync(string key, StoredResponse response, CancellationToken cancellationToken)
    {
        // Only this unit of work's own reservation can be completed: after a successful commit it is
        // tracked as Unchanged; after a failed one the tracker was cleared (CommitAsync) and there is no
        // row to complete. A reservation still Added was never committed (the command failed before the
        // commit): it must not be inserted now, or a failed request would block its own retry.
        EntityEntry<IdempotencyKey>? reservation = context.ChangeTracker.Entries<IdempotencyKey>()
            .FirstOrDefault(entry => string.Equals(entry.Entity.Key, key, StringComparison.Ordinal));

        if (reservation is null)
        {
            return;
        }

        if (reservation.State == EntityState.Added)
        {
            reservation.State = EntityState.Detached;
            return;
        }

        reservation.Entity.Complete(response, timeProvider.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken);
    }
}
