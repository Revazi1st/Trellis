using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Trellis.Persistence.Identity;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    /// <summary>
    /// Identity's own name for the unique index on the normalized user name (the e-mail), restated here so
    /// the constraint map (<see cref="UniqueConstraintErrors"/>) recognises a lost registration race by a
    /// name this code pins rather than one Identity could rename underneath it. The model is unchanged.
    /// </summary>
    public const string UserNameIndexName = "UserNameIndex";

    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
        => builder.HasIndex(static user => user.NormalizedUserName).HasDatabaseName(UserNameIndexName).IsUnique();
}
