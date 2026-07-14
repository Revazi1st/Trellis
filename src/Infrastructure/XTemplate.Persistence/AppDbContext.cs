using Microsoft.EntityFrameworkCore;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Persistence;

/// <summary>The single write model for this service; implements the SharedKernel commit boundary (IUnitOfWork).</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // One line per strongly-typed ID (ADR-0005). Adding an aggregate? Register its ID here.
        configurationBuilder.Properties<InvoiceId>().HaveConversion<StronglyTypedIdValueConverter<InvoiceId>>();
        configurationBuilder.Properties<InvoiceLineId>().HaveConversion<StronglyTypedIdValueConverter<InvoiceLineId>>();
    }
}
