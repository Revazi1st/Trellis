using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;

namespace Trellis.Persistence.Configurations;

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("InvoiceLines");

        builder.HasKey(static l => l.Id);
        builder.Property(static l => l.Id).ValueGeneratedNever();

        builder.Property(static l => l.Description).HasMaxLength(InvoiceLine.DescriptionMaxLength);

        builder.ComplexProperty(static l => l.Amount, static amount =>
        {
            amount.Property(static m => m.Amount).HasColumnName("Amount").HasPrecision(18, 2);
            amount.Property(static m => m.Currency).HasColumnName("Currency").HasMaxLength(Currency.CodeLength);
        });
    }
}
