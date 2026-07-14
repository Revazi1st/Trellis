using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XTemplate.Domain.Invoices;

namespace XTemplate.Persistence.Configurations;

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("InvoiceLines");

        builder.HasKey(static l => l.Id);
        builder.Property(static l => l.Id).ValueGeneratedNever();

        builder.Property(static l => l.Description).HasMaxLength(500);

        builder.ComplexProperty(static l => l.Amount, static amount =>
        {
            amount.Property(static m => m.Amount).HasColumnName("Amount").HasPrecision(18, 2);
            amount.Property(static m => m.Currency).HasColumnName("Currency").HasMaxLength(3);
        });
    }
}
