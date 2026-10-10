using Microsoft.AspNetCore.Http;
using Trellis.Application.Abstractions;

namespace Trellis.Api.Common.Http;

/// <summary>
/// Resolves the tenant from the authenticated principal's <c>tenant</c> claim — and from nothing else.
/// The claim is minted by the API from the user's persisted membership, so it cannot be chosen by
/// the caller; a request header could be, which is why none is read (ADR-0022). Anonymous requests
/// have no tenant (null = system scope); tokens without the claim never get this far
/// (AuthenticationExtensions rejects them).
/// <para>
/// <see cref="IHttpContextAccessor"/> is an AsyncLocal, and not every flow a request-scoped service
/// runs on carries it: HybridCache runs <c>GetOrCreateAsync</c> factories on the thread pool without
/// the caller's ExecutionContext, so a query handler asking for the tenant inside one sees no request.
/// Reading live there would silently answer "no tenant" (= system scope). So the principal's tenant is
/// read live whenever the request is visible and remembered for the scope; a detached flow gets the
/// value the request flow last resolved — for the cache-aside endpoints that is the cache key, which
/// is computed on the request flow before the factory runs. A principal cannot change within a request.
/// </para>
/// </summary>
internal sealed class TenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    /// <summary>The ambient tenant when one is set; otherwise the principal's, remembered per scope (see the class remarks).</summary>
    public string? TenantId
    {
        get
        {
            // Non-request flows (outbox dispatch) restore the originating tenant ambiently.
            if (AmbientTenant.CurrentTenantId is { } ambient)
            {
                return ambient;
            }

            // `field` is this property's backing store: the principal's tenant as last read on a
            // flow that could see the request. A detached flow (a HybridCache factory) returns it.
            if (httpContextAccessor.HttpContext is { } httpContext)
            {
                string? claim = httpContext.User.FindFirst(ITenantContext.ClaimType)?.Value;
                field = string.IsNullOrWhiteSpace(claim) ? null : claim;
            }

            return field;
        }
    }
}
