using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XTemplate.Application.Abstractions;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Persistence.Interceptors;

/// <summary>
/// Stamps IAuditable entities from TimeProvider + ICurrentUser. Aggregate-root aware: adding, changing or
/// removing a child entity also stamps its auditable root, because touching children IS modifying the aggregate.
/// Registered last so it sees everything the other interceptors changed. Writes go through the entry API —
/// DetectChanges has already run by interception time, so plain property sets on Unchanged/Modified entries
/// would not be picked up.
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
        string? user = currentUser.UserName;

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
