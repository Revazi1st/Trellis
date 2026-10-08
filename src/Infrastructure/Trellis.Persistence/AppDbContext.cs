#if (UseMultitenancy)
using System.Linq.Expressions;
#endif
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
#if (UseMultitenancy)
using Microsoft.EntityFrameworkCore.Metadata.Builders;
#endif
using Trellis.Domain.Invoices;
using Trellis.Persistence.Identity;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Results;

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

#if (UseMultitenancy)
    /// <summary>Read by the "Tenant" query filters; a member on the context so EF parameterises it per query.</summary>
    internal string? CurrentTenantId => tenantContext.TenantId;
#endif

    /// <inheritdoc />
    public async Task<Result> CommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            // A stale xmin is an expected outcome (ADR-0004), not a fault: it travels as a Result to the
            // 409 seam instead of an Error log, an errored span and an exception handler. The rejected
            // entries are dropped so nothing stale leaks into later work on this scoped context.
            ChangeTracker.Clear();
            return Result.Failure(UnitOfWorkErrors.ConcurrencyConflict);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

#if (UseMultitenancy)
        // Tenant membership is a required attribute of the user (ADR-0022). Identity tables are
        // deliberately NOT tenant-filtered: login, lockout and refresh run before a tenant is known.
        builder.Entity<ApplicationUser>(static user =>
        {
            user.Property(static u => u.TenantId).IsRequired().HasMaxLength(64);
            user.HasIndex(static u => u.TenantId);
        });

        // Convention, not configuration: EVERY aggregate root gets the TenantId shadow column, its
        // index and the named "Tenant" filter. A new aggregate is isolated without anyone remembering
        // to wire it; a deliberately shared aggregate is a design decision that must be made here, on
        // purpose. IHasDomainEvents is what AggregateRoot<> implements (and what OutboxWriteInterceptor
        // already uses to recognise aggregates). Named so callers can opt out explicitly:
        // IgnoreQueryFilters(["Tenant"]) — never the bare IgnoreQueryFilters(), which drops it silently.
        foreach (Type aggregateType in builder.Model.GetEntityTypes()
                     .Select(static entityType => entityType.ClrType)
                     .Where(static clrType => typeof(IHasDomainEvents).IsAssignableFrom(clrType))
                     .ToList())
        {
            EntityTypeBuilder aggregate = builder.Entity(aggregateType);
            aggregate.Property<string>(Interceptors.TenantInterceptor.TenantIdProperty).HasMaxLength(64);
            aggregate.HasIndex(Interceptors.TenantInterceptor.TenantIdProperty);

            // e => EF.Property<string>(e, "TenantId") == this.CurrentTenantId
            // The constant is this DbContext: EF swaps it for the live context at query time, so the
            // tenant is evaluated per query, not frozen into the cached model.
            ParameterExpression entity = Expression.Parameter(aggregateType, "e");
            Expression tenantColumn = Expression.Call(
                typeof(EF), nameof(EF.Property), [typeof(string)],
                entity, Expression.Constant(Interceptors.TenantInterceptor.TenantIdProperty));
            Expression currentTenant = Expression.Property(
                Expression.Constant(this, typeof(AppDbContext)), nameof(CurrentTenantId));
            aggregate.HasQueryFilter("Tenant", Expression.Lambda(Expression.Equal(tenantColumn, currentTenant), entity));
        }
#endif
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // One line per strongly-typed ID (ADR-0005). Adding an aggregate? Register its ID here.
        configurationBuilder.Properties<InvoiceId>().HaveConversion<StronglyTypedIdValueConverter<InvoiceId>>();
        configurationBuilder.Properties<InvoiceLineId>().HaveConversion<StronglyTypedIdValueConverter<InvoiceLineId>>();
    }
}
