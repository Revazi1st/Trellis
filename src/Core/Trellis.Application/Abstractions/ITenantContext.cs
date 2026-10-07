namespace Trellis.Application.Abstractions;

/// <summary>
/// The tenant the current request belongs to. Used by the persistence tenant query filter and the
/// interceptor that stamps new rows. Only present when the multi-tenancy template option is enabled.
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// The access-token claim that carries the tenant. It is the ONLY request input the HTTP resolver
    /// trusts (ADR-0022); the token provider mints it from the user's persisted membership.
    /// </summary>
    const string ClaimType = "tenant";

    /// <summary>The current tenant identifier, or null when the request has no tenant (e.g. system work).</summary>
    string? TenantId { get; }
}
