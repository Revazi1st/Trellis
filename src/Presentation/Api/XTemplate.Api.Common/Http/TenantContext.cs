using Microsoft.AspNetCore.Http;
using XTemplate.Application.Abstractions;

namespace XTemplate.Api.Common.Http;

/// <summary>
/// Resolves the tenant from the authenticated principal's <c>tenant</c> claim, falling back to the
/// <c>X-Tenant-Id</c> header. Claim first: a header alone must never let a caller read another tenant's data.
/// </summary>
internal sealed class TenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    public const string HeaderName = "X-Tenant-Id";
    public const string ClaimType = "tenant";

    public string? TenantId
    {
        get
        {
            // Non-request flows (outbox dispatch) restore the originating tenant ambiently.
            if (AmbientTenant.CurrentTenantId is { } ambient)
            {
                return ambient;
            }

            HttpContext? context = httpContextAccessor.HttpContext;
            if (context is null)
            {
                return null;
            }

            string? fromClaim = context.User.FindFirst(ClaimType)?.Value;
            if (!string.IsNullOrWhiteSpace(fromClaim))
            {
                return fromClaim;
            }

            return context.Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrWhiteSpace(header)
                ? header.ToString()
                : null;
        }
    }
}
