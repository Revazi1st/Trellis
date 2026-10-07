using Microsoft.AspNetCore.Identity;

namespace Trellis.Persistence.Identity;

/// <summary>The ASP.NET Core Identity user, keyed by GUID to match the rest of the model.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
#if (UseMultitenancy)
    /// <summary>
    /// The one tenant this user belongs to (ADR-0022). Minted into every access token as the
    /// "tenant" claim; the API never accepts a tenant from anywhere else. Identity itself is
    /// global — this is an attribute of the user, not a filter on the user table.
    /// </summary>
    public string TenantId { get; set; } = null!;
#endif
}
