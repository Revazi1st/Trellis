using XTemplate.Application.Abstractions;

namespace XTemplate.MigrationService;

/// <summary>Audit identity for changes made by the migration/seeding worker.</summary>
internal sealed class SystemCurrentUser : ICurrentUser
{
    public Guid? UserId => null;

    public string? UserName => "system";
}
