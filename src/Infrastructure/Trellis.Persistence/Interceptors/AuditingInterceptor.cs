using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Trellis.Application.Abstractions;
using Trellis.SharedKernel.Domain;

namespace Trellis.Persistence.Interceptors;

/// <summary>
/// Stamps IAuditable entities from TimeProvider + ICurrentUser. Aggregate-root aware: adding, changing or
/// removing a child entity also stamps its auditable root, because touching children IS modifying the aggregate.
/// Registered after the outbox and soft-delete interceptors so it sees everything they changed. Writes go through the entry API —
/// DetectChanges has already run by interception time, so plain property sets on Unchanged/Modified entries
/// would not be picked up.
/// <para>
/// The user columns hold the acting user's id, not their name: the id never changes, and the name is the
/// e-mail, which does and which a column copied into every row and backup should not hold. A caller without
/// an id (the migration job's <c>SystemCurrentUser</c>) is stamped with its name, <c>system</c>.
/// </para>
/// </summary>
public sealed class AuditingInterceptor(TimeProvider timeProvider, ICurrentUser currentUser) : SaveChangesInterceptor
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

        DateTimeOffset now = timeProvider.GetUtcNow();
        string? user = currentUser.UserId?.ToString() ?? currentUser.UserName;

        foreach (EntityEntry<IAuditable> entry in context.ChangeTracker.Entries<IAuditable>().ToList())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.CreatedBy = user;
            }
            else if (entry.State == EntityState.Modified)
            {
                StampModified(entry, now, user);
            }
        }

        // Child entities are not IAuditable themselves — their changes stamp the owning auditable root.
        foreach (EntityEntry dependent in context.ChangeTracker.Entries()
                     .Where(static e => e.Entity is not IAuditable
                         && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                     .ToList())
        {
            foreach (var foreignKey in dependent.Metadata.GetForeignKeys())
            {
                if (!typeof(IAuditable).IsAssignableFrom(foreignKey.PrincipalEntityType.ClrType))
                {
                    continue;
                }

                object?[] foreignKeyValues = [.. foreignKey.Properties.Select(p => dependent.Property(p.Name).CurrentValue)];
                if (Array.Exists(foreignKeyValues, static v => v is null))
                {
                    continue;
                }

                EntityEntry? principal = context.ChangeTracker.Entries()
                    .FirstOrDefault(e => e.Metadata == foreignKey.PrincipalEntityType
                        && foreignKeyValues.SequenceEqual(
                            foreignKey.PrincipalKey.Properties.Select(p => e.Property(p.Name).CurrentValue)));

                if (principal is { State: EntityState.Unchanged })
                {
                    StampModified(principal, now, user);
                }
            }
        }
    }

    private static void StampModified(EntityEntry entry, DateTimeOffset now, string? user)
    {
        entry.Property(nameof(IAuditable.ModifiedAtUtc)).CurrentValue = now;
        entry.Property(nameof(IAuditable.ModifiedBy)).CurrentValue = user;
    }
}
