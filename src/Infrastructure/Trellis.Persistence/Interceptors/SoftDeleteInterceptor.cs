using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Trellis.SharedKernel.Domain;

namespace Trellis.Persistence.Interceptors;

/// <summary>
/// Turns hard deletes of ISoftDeletable aggregates into DeletedAtUtc updates, and keeps their
/// cascade-deleted children alive so the soft-deleted aggregate stays complete and restorable.
/// </summary>
public sealed class SoftDeleteInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        List<EntityEntry<ISoftDeletable>> softDeleted = [.. context.ChangeTracker
            .Entries<ISoftDeletable>()
            .Where(static entry => entry.State == EntityState.Deleted)];

        if (softDeleted.Count == 0)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (EntityEntry<ISoftDeletable> entry in softDeleted)
        {
            entry.State = EntityState.Modified;
            entry.Entity.DeletedAtUtc = now;
        }

        // Rescue children that the root's Remove() cascade marked for deletion. Matched by FK value so an
        // individually-orphaned child (removed from its collection) would still be deleted for real.
        foreach (EntityEntry dependent in context.ChangeTracker.Entries().Where(static e => e.State == EntityState.Deleted).ToList())
        {
            bool belongsToSoftDeletedRoot = dependent.Metadata.GetForeignKeys().Any(fk =>
                typeof(ISoftDeletable).IsAssignableFrom(fk.PrincipalEntityType.ClrType) &&
                softDeleted.Any(root =>
                    root.Metadata.ClrType == fk.PrincipalEntityType.ClrType &&
                    fk.Properties
                        .Select(p => dependent.Property(p.Name).CurrentValue)
                        .SequenceEqual(fk.PrincipalKey.Properties.Select(p => root.Property(p.Name).CurrentValue))));

            if (belongsToSoftDeletedRoot)
            {
                dependent.State = EntityState.Unchanged;
            }
        }
    }
}
