using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace XTemplate.Persistence.Outbox;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(static m => m.Id);
        builder.Property(static m => m.Type).HasMaxLength(256);
        builder.Property(static m => m.Content).HasColumnType("jsonb");
        builder.Property(static m => m.TraceParent).HasMaxLength(64);
#if (UseMultitenancy)
        builder.Property(static m => m.TenantId).HasMaxLength(64);
#endif

        // Partial index over exactly the processor's scan (pending, due, oldest first): it stays
        // tiny no matter how much processed history accumulates.
        builder.HasIndex(static m => new { m.NextAttemptAtUtc, m.OccurredAtUtc })
            .HasFilter("\"Status\" = 0");
    }
}
