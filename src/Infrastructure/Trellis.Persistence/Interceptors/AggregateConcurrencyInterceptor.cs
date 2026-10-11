using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Trellis.SharedKernel.Domain;

namespace Trellis.Persistence.Interceptors;

/// <summary>
/// Keeps an aggregate's optimistic-concurrency token honest when only its children change (ADR-0006,
/// amendment of 2026-10-11). The token (Postgres <c>xmin</c>) belongs to the root's row, and EF writes that
/// row only when one of its own columns changes: adding a line to a loaded invoice would otherwise INSERT
/// the line without checking, or bumping, the invoice's version, and two concurrent edits of one
/// aggregate would both commit.
/// <para>
/// So every <see cref="EntityState.Unchanged"/> aggregate root (an <see cref="IHasDomainEvents"/> entity)
/// with an added, modified or deleted child gets one real column marked modified — its current value is
/// written back — and its UPDATE then carries the token check and bumps the token. A child is an entity
/// the root reaches through a navigation (<c>Invoice.Lines</c>), followed up through grandchildren; a
/// foreign key to another aggregate without such a navigation promotes nothing. Auditable roots happen to
/// be stamped by <see cref="AuditingInterceptor"/> already; this pass makes the guarantee independent of
/// <see cref="IAuditable"/>. <c>AggregateConcurrencyTests</c> checks the model half (every root has a token
/// and a column to promote) and the behaviour.
/// </para>
/// <para>
/// Stateless, so one instance serves every context: <c>AppDbContext.OnConfiguring</c> attaches it after
/// the interceptors the host registers, which means no context instance runs without it.
/// </para>
/// </summary>
public sealed class AggregateConcurrencyInterceptor : SaveChangesInterceptor
{
    public static readonly AggregateConcurrencyInterceptor Instance = new();

    private AggregateConcurrencyInterceptor()
    {
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Promote(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Promote(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// The column the promotion writes back on <paramref name="aggregateRoot"/>: a mapped, non-key,
    /// non-generated property the UPDATE may set. Null means the root cannot be promoted, which the model
    /// test refuses before any save would.
    /// </summary>
    internal static IProperty? PromotableProperty(IEntityType aggregateRoot)
        => aggregateRoot.GetProperties().FirstOrDefault(static property =>
            !property.IsKey()
            && !property.IsConcurrencyToken
            && !property.IsShadowProperty()
            && property.ValueGenerated == ValueGenerated.Never
            && property.GetAfterSaveBehavior() == PropertySaveBehavior.Save);

    private static void Promote(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        List<EntityEntry> changedChildren = [.. context.ChangeTracker.Entries()
            .Where(static entry => entry.Entity is not IHasDomainEvents
                && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)];

        if (changedChildren.Count == 0)
        {
            return;
        }

        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        foreach (EntityEntry child in changedChildren)
        {
            PromoteOwnersOf(context, child, visited);
        }
    }

    /// <summary>Walks from <paramref name="dependent"/> up the navigations that own it until it reaches aggregate roots.</summary>
    private static void PromoteOwnersOf(DbContext context, EntityEntry dependent, HashSet<object> visited)
    {
        foreach (IForeignKey ownership in dependent.Metadata.GetForeignKeys().Where(static fk => fk.PrincipalToDependent is not null))
        {
            if (FindPrincipal(context, dependent, ownership) is not { } principal || !visited.Add(principal.Entity))
            {
                continue;
            }

            if (principal.Entity is not IHasDomainEvents)
            {
                PromoteOwnersOf(context, principal, visited);
            }
            else if (principal.State == EntityState.Unchanged)
            {
                IProperty column = PromotableProperty(principal.Metadata)
                    ?? throw new InvalidOperationException(
                        $"The aggregate root '{principal.Metadata.DisplayName()}' has no column the concurrency promotion can write back, so a child-only change would skip its concurrency token.");
                principal.Property(column.Name).IsModified = true;
            }
        }
    }

    private static EntityEntry? FindPrincipal(DbContext context, EntityEntry dependent, IForeignKey foreignKey)
    {
        object?[] foreignKeyValues = [.. foreignKey.Properties.Select(property => dependent.Property(property.Name).CurrentValue)];
        if (Array.Exists(foreignKeyValues, static value => value is null))
        {
            return null;
        }

        return context.ChangeTracker.Entries().FirstOrDefault(entry =>
            entry.Metadata == foreignKey.PrincipalEntityType
            && foreignKeyValues.SequenceEqual(foreignKey.PrincipalKey.Properties.Select(property => entry.Property(property.Name).CurrentValue)));
    }
}
