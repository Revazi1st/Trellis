using Microsoft.AspNetCore.Http;
using Trellis.Application.Abstractions;

namespace Trellis.Api.Common.Http;

/// <summary>
/// Resolves the tenant from the authenticated principal's <c>tenant</c> claim — and from nothing else.
/// The claim is minted by the API from the user's persisted membership, so it cannot be chosen by
/// the caller; a request header could be, which is why none is read (ADR-0022). Anonymous requests
/// have no tenant (null = system scope); tokens without the claim never get this far
/// (AuthenticationExtensions rejects them).
/// </summary>
internal sealed class TenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    public string? TenantId
    {
        get
        {
            // Non-request flows (outbox dispatch) restore the originating tenant ambiently.
            if (AmbientTenant.CurrentTenantId is { } ambient)
            {
                return ambient;
            }

            string? claim = httpContextAccessor.HttpContext?.User.FindFirst(ITenantContext.ClaimType)?.Value;
            return string.IsNullOrWhiteSpace(claim) ? null : claim;
        }
    }
}
