using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XTemplate.Domain.Invoices;

namespace XTemplate.Persistence.Configurations;

/// <summary>The reference aggregate mapping: value objects as complex types, whole-aggregate loading, named soft-delete filter (ADR-0006).</summary>
internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoices");

        builder.HasKey(static i => i.Id);
        builder.Property(static i => i.Id).ValueGeneratedNever();

        // Optimistic concurrency via the Postgres 'xmin' system column — no schema column added.
        builder.Property<uint>("xmin")
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

        builder.Property(static i => i.CustomerName).HasMaxLength(200);
        builder.Property(static i => i.Currency).HasMaxLength(3);
        builder.Property(static i => i.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(static i => i.CreatedBy).HasMaxLength(256);
        builder.Property(static i => i.ModifiedBy).HasMaxLength(256);

        // Value object as EF complex type (table splitting) — the EF 10 recommended mapping for VOs.
        builder.ComplexProperty(static i => i.CustomerEmail, static email =>
            email.Property(static e => e.Value).HasColumnName("CustomerEmail").HasMaxLength(320));

        // Aggregates load whole: children are AutoInclude'd and reached through the backing field (no public mutable collection).
        builder.HasMany(static i => i.Lines).WithOne().HasForeignKey("InvoiceId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(static i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        // EF 10 named query filter — selectively disabled via IgnoreQueryFilters(["SoftDeleted"]).
        builder.HasQueryFilter("SoftDeleted", static i => i.DeletedAtUtc == null);

        builder.HasIndex(static i => i.Status);
    }
}
