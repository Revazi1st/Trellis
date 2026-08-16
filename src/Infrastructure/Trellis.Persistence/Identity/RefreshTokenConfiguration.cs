using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Trellis.Persistence.Identity;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(static t => t.Id);
        builder.Property(static t => t.TokenHash).HasMaxLength(64);
        builder.HasIndex(static t => t.TokenHash).IsUnique();
        builder.HasIndex(static t => t.UserId);

        // Cascade from AspNetUsers: deleting a user must not leave their live tokens behind.
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(static t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
