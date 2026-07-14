namespace XTemplate.MigrationService;

/// <summary>
/// Bootstrap admin credentials for a fresh database, bound from the "Seed" configuration section.
/// Override in every real environment (env vars / user-secrets); the defaults are for local dev only.
/// </summary>
internal sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public string AdminEmail { get; init; } = "admin@xtemplate.dev";

    public string AdminPassword { get; init; } = "Admin123!";
}
