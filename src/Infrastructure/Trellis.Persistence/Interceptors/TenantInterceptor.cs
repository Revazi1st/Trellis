using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Trellis.Application.Abstractions;

namespace Trellis.Persistence.Interceptors;

/// <summary>
/// Stamps the <c>TenantId</c> shadow property on newly added rows so they belong to the current tenant.
/// Reads are isolated by the "Tenant" query filter on the DbContext. Multi-tenancy option only.
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
            .Where(static e => e.State == EntityState.Added && e.Metadata.FindProperty(TenantIdProperty) is not null);

        foreach (EntityEntry entry in tenantScoped)
        {
            entry.Property(TenantIdProperty).CurrentValue = tenantId;
        }
    }
}
