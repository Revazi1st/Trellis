using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace XTemplate.Persistence.Outbox;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(static m => m.Id);
        builder.Property(static m => m.Type).HasMaxLength(512);
        builder.Property(static m => m.Content).HasColumnType("jsonb");
        // Index the unprocessed-oldest-first scan the processor runs.
        builder.HasIndex(static m => new { m.ProcessedAtUtc, m.OccurredAtUtc });
    }
}
