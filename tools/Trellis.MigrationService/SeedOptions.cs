using System.ComponentModel.DataAnnotations;

namespace Trellis.MigrationService;

/// <summary>
/// Bootstrap admin credentials for a fresh database, bound from the "Seed" configuration section and
/// validated at boot (<c>Program.cs</c>: <c>ValidateDataAnnotations().ValidateOnStart()</c>), so a typo
/// stops the worker with the key's name instead of seeding an admin nobody can sign in as.
/// Override in every real environment (env vars / user-secrets); the defaults are for local dev only.
/// </summary>
internal sealed class SeedOptions
{
    public const string SectionName = "Seed";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2068:Hard-coded credentials are security-sensitive",
        Justification = "Local-dev bootstrap value only; the worker refuses to seed it outside Development.")]
    public const string DefaultAdminPassword = "Admin123!";

    [Required(AllowEmptyStrings = false, ErrorMessage = "Seed:AdminEmail is required.")]
    [EmailAddress(ErrorMessage = "Seed:AdminEmail must be an e-mail address.")]
    public string AdminEmail { get; init; } = "admin@example.com";

    /// <summary>
    /// The length floor matches Identity's <c>RequiredLength</c>; Identity's other password rules
    /// (digit, case, symbol) are checked when the admin is created, and a rejection fails the worker.
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Seed:AdminPassword is required.")]
    [MinLength(8, ErrorMessage = "Seed:AdminPassword must be at least {1} characters.")]
    public string AdminPassword { get; init; } = DefaultAdminPassword;

#if (UseMultitenancy)
    /// <summary>
    /// The tenant the bootstrap admin — and, in Development, the demo invoices — belong to (ADR-0022).
    /// Bounded like the <c>TenantId</c> columns it is written to (64 characters).
    /// </summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Seed:TenantId is required.")]
    [MaxLength(64, ErrorMessage = "Seed:TenantId must be at most {1} characters.")]
    public string TenantId { get; init; } = "default";
#endif

    /// <summary>The well-known local-dev password must never reach a real environment (guarded in the worker).</summary>
    public bool UsesDefaultAdminPassword => string.Equals(AdminPassword, DefaultAdminPassword, StringComparison.Ordinal);
}
