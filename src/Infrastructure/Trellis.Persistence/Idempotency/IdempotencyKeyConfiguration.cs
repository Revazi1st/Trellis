using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Trellis.Persistence.Idempotency;

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    /// <summary>Named explicitly because <c>AppDbContext.CommitAsync</c> recognises the commit race by this constraint.</summary>
    public const string PrimaryKeyName = "PK_IdempotencyKeys";

    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("IdempotencyKeys");
        builder.HasKey(static k => k.Key).HasName(PrimaryKeyName);
        builder.Property(static k => k.Key).HasMaxLength(128);
        builder.Property(static k => k.RequestFingerprint).HasMaxLength(64);
        builder.Property(static k => k.ContentType).HasMaxLength(256);
        builder.Property(static k => k.Location).HasMaxLength(2048);
    }
}
