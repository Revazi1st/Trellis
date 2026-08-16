#if (UseMultitenancy)
using XTemplate.Application.Abstractions;
#else
using System.Diagnostics.CodeAnalysis;
#endif

namespace XTemplate.Api.Common.Caching;

/// <summary>
/// Cache-key conventions shared by read and write endpoints so invalidation stays in sync with reads.
/// Injected (scoped) rather than static: with multi-tenancy on, every key carries the tenant — a
/// cache hit bypasses the EF "Tenant" query filter, so an unscoped key would serve one tenant's
/// data to another.
/// </summary>
public sealed class CacheKeys
{
#if (UseMultitenancy)
    private readonly ITenantContext _tenantContext;

    public CacheKeys(ITenantContext tenantContext) => _tenantContext = tenantContext;

    public string Invoice(Guid id) => $"tenant:{_tenantContext.TenantId ?? "system"}:invoice:{id}";
#else
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Instance-shaped to match the multi-tenant variant of this class; callers inject it either way.")]
    [SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static",
        Justification = "Instance-shaped to match the multi-tenant variant of this class.")]
    public string Invoice(Guid id) => $"invoice:{id}";
#endif
}
