using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Trellis.Application.Abstractions;
using Trellis.SharedKernel.Domain;

namespace Trellis.Persistence.Interceptors;

/// <summary>
/// Stamps the <c>TenantId</c> shadow property on newly added aggregate roots so they belong to the
/// current tenant. Reads are isolated by the "Tenant" query filter on the DbContext. Multi-tenancy
/// option only.
/// <para>
/// Only the shadow column the aggregate-root convention adds (<c>AppDbContext.OnModelCreating</c>) is
/// stamped. A CLR <c>TenantId</c> property is a value the code assigns on purpose and is never
/// overwritten: <c>ApplicationUser.TenantId</c> is the user's membership, which may name a tenant
/// other than the caller's, and <c>OutboxMessage.TenantId</c> is written by the outbox interceptor.
/// </para>
/// </summary>
public sealed class TenantInterceptor(ITenantContext tenantContext) : SaveChangesInterceptor
{
    public const string TenantIdProperty = "TenantId";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null || tenantContext.TenantId is not { Length: > 0 } tenantId)
        {
            return;
        }

        IEnumerable<EntityEntry> tenantScoped = context.ChangeTracker.Entries()
            .Where(static e => e.State == EntityState.Added
                && e.Entity is IHasDomainEvents
                && e.Metadata.FindProperty(TenantIdProperty) is { } property
                && property.IsShadowProperty());

        foreach (EntityEntry entry in tenantScoped)
        {
            entry.Property(TenantIdProperty).CurrentValue = tenantId;
        }
    }
}
