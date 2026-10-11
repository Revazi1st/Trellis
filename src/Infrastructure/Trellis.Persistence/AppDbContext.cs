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
        catch (DbUpdateException exception) when (UniqueConstraintErrors.TryMap(exception, out Error? expected))
        {
            // A registered unique constraint decided a race (ADR-0018) — two first requests with one
            // Idempotency-Key, say: the loser's whole unit of work (aggregate, outbox rows, reservation)
            // rolled back with this exception and the winner's row stands. Expected, so a Result, not a
            // fault. Any unregistered unique violation is a fault the model did not foresee and still throws.
            ChangeTracker.Clear();
            return Result.Failure(expected);
        }
    }

    /// <summary>
    /// Concurrency at the aggregate boundary is a property of the model, not of a host's wiring: the
    /// promotion pass is attached here, after whatever interceptors the host registered, so every instance
    /// of this context — the API's, the migration worker's, a test's — runs it (ADR-0006).
    /// </summary>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.AddInterceptors(Interceptors.AggregateConcurrencyInterceptor.Instance);

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

        // Every instant is written as UTC: Npgsql rejects a non-zero offset for timestamptz, so a client's
        // "+02:00" would otherwise fail the save. Covers DateTimeOffset? too; the column type is unchanged.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }
}
