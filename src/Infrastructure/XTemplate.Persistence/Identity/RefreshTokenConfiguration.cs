using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace XTemplate.Persistence.Identity;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(static t => t.Id);
        builder.Property(static t => t.TokenHash).HasMaxLength(64);
        builder.HasIndex(static t => t.TokenHash).IsUnique();
        builder.HasIndex(static t => t.UserId);
    }
}
