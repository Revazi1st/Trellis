using Trellis.Application.Abstractions;

namespace Trellis.Persistence;

/// <summary>
/// The <see cref="ITenantContext"/> for hosts that have no HTTP request — the MigrationService,
/// background workers, subcutaneous tests. HTTP hosts get the request-scoped implementation from
/// Api.Common instead. Honours the <see cref="AmbientTenant"/> override (outbox dispatch restores
/// the originating tenant there); otherwise no tenant — the "Tenant" query filter matches only
/// rows with a NULL TenantId and the TenantInterceptor leaves new rows unstamped: system-scoped data.
/// </summary>
public sealed class SystemTenantContext : ITenantContext
{
    public string? TenantId => AmbientTenant.CurrentTenantId;
}
