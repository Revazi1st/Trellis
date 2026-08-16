namespace Trellis.Application.Abstractions;

/// <summary>
/// The tenant the current request belongs to. Used by the persistence tenant query filter and the
/// interceptor that stamps new rows. Only present when the multi-tenancy template option is enabled.
/// </summary>
public interface ITenantContext
{
    /// <summary>The current tenant identifier, or null when the request has no tenant (e.g. system work).</summary>
    string? TenantId { get; }
}
