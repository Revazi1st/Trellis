namespace Trellis.MigrationService;

/// <summary>
/// Bootstrap admin credentials for a fresh database, bound from the "Seed" configuration section.
/// Override in every real environment (env vars / user-secrets); the defaults are for local dev only.
/// </summary>
internal sealed class SeedOptions
{
    public const string SectionName = "Seed";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2068:Hard-coded credentials are security-sensitive",
        Justification = "Local-dev bootstrap value only; the worker refuses to seed it outside Development.")]
    public const string DefaultAdminPassword = "Admin123!";

    public string AdminEmail { get; init; } = "admin@trellis.dev";

    public string AdminPassword { get; init; } = DefaultAdminPassword;

    /// <summary>The well-known local-dev password must never reach a real environment (guarded in the worker).</summary>
    public bool UsesDefaultAdminPassword => string.Equals(AdminPassword, DefaultAdminPassword, StringComparison.Ordinal);
}
