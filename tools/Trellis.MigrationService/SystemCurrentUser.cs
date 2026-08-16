using Trellis.Application.Abstractions;

namespace Trellis.MigrationService;

/// <summary>Audit identity for changes made by the migration/seeding worker.</summary>
internal sealed class SystemCurrentUser : ICurrentUser
{
    public Guid? UserId => null;

    public string? UserName => "system";
}
