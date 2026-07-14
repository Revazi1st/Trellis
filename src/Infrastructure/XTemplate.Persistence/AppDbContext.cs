using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using XTemplate.Domain.Invoices;
using XTemplate.Persistence.Identity;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Persistence;

/// <summary>The single write model for this service: domain aggregates plus ASP.NET Core Identity. Implements the SharedKernel commit boundary.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IUnitOfWork
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // One line per strongly-typed ID (ADR-0005). Adding an aggregate? Register its ID here.
        configurationBuilder.Properties<InvoiceId>().HaveConversion<StronglyTypedIdValueConverter<InvoiceId>>();
        configurationBuilder.Properties<InvoiceLineId>().HaveConversion<StronglyTypedIdValueConverter<InvoiceLineId>>();
    }
}
