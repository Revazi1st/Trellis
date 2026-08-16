using XTemplate.Application.Abstractions;

namespace XTemplate.Persistence;

/// <summary>
/// A no-tenant <see cref="ITenantContext"/> for hosts that have no HTTP request — the MigrationService,
/// background workers, subcutaneous tests. HTTP hosts get the request-scoped implementation from
/// Api.Common instead. With no tenant, the "Tenant" query filter matches only rows with a NULL
/// TenantId and the TenantInterceptor leaves new rows unstamped: system-scoped data.
/// </summary>
public sealed class SystemTenantContext : ITenantContext
{
    public string? TenantId => null;
}
