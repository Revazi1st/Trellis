using Microsoft.AspNetCore.Identity;

namespace Trellis.Persistence.Identity;

/// <summary>The ASP.NET Core Identity user, keyed by GUID to match the rest of the model.</summary>
public sealed class ApplicationUser : IdentityUser<Guid>;
