using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Trellis.Domain.Invoices;
using Trellis.Persistence.Identity;
using Trellis.SharedKernel.Domain;

namespace Trellis.Persistence;

/// <summary>The single write model for this service: domain aggregates plus ASP.NET Core Identity. Implements the SharedKernel commit boundary.</summary>
public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options
#if (UseMultitenancy)
    , Trellis.Application.Abstractions.ITenantContext tenantContext
#endif
    ) : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IUnitOfWork
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

#if (UseMultitenancy)
        // Named filter so callers can opt out explicitly: IgnoreQueryFilters(["Tenant"]).
        // The lambda captures this DbContext, so EF re-evaluates the tenant per query.
        builder.Entity<Invoice>().HasQueryFilter(
            "Tenant",
            invoice => EF.Property<string>(invoice, Interceptors.TenantInterceptor.TenantIdProperty) == tenantContext.TenantId);
#endif
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // One line per strongly-typed ID (ADR-0005). Adding an aggregate? Register its ID here.
        configurationBuilder.Properties<InvoiceId>().HaveConversion<StronglyTypedIdValueConverter<InvoiceId>>();
        configurationBuilder.Properties<InvoiceLineId>().HaveConversion<StronglyTypedIdValueConverter<InvoiceLineId>>();
    }
}
