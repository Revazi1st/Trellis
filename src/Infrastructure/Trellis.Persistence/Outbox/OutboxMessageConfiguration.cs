using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Trellis.Persistence.Outbox;

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

        // The lease is also the fence (ADR-0023): as the concurrency token, NextAttemptAtUtc puts
        // `AND "NextAttemptAtUtc" = <the lease this processor stamped>` under every bookkeeping UPDATE —
        // checkpoint, Processed, back-off, dead letter. A processor whose lease expired and was claimed
        // again (a later, so different, lease) affects no row and books lease_lost instead of
        // resurrecting a Processed row or dead-lettering the new owner's. Same column, no migration of
        // the table: only the model snapshot carries the annotation.
        builder.Property(static m => m.NextAttemptAtUtc).IsConcurrencyToken();

        // Partial index over exactly the processor's scan (pending, due, oldest first): it stays
        // tiny no matter how much processed history accumulates.
        builder.HasIndex(static m => new { m.NextAttemptAtUtc, m.OccurredAtUtc })
            .HasFilter("\"Status\" = 0");
    }
}
