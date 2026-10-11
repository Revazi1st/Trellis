using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Trellis.Api.Common.Hardening;

/// <summary>
/// Security response headers for a JSON API (ADR-0021, OWASP REST Security cheat sheet). They are
/// set when the response starts, so every response carries them, including the 401/403/429 problem
/// documents written by the authentication and rate-limiting callbacks and the exception handler's 500.
/// <list type="bullet">
/// <item><c>X-Content-Type-Options: nosniff</c>: a browser never reinterprets a JSON body as script or HTML.</item>
/// <item><c>Referrer-Policy: no-referrer</c>: URLs (which carry ids) never leak to a third party.</item>
/// <item><c>X-Frame-Options: DENY</c> + <c>Content-Security-Policy: frame-ancestors 'none'</c>: no API response
/// (nor the development-only Scalar page) can be framed. Only <c>frame-ancestors</c> is set, so Scalar still loads.</item>
/// <item><c>Cache-Control: no-store</c> on every <c>/api/</c> response, problems included, unless the endpoint set
/// its own: per-user data (an invoice, a 403 that says it exists) must not sit in a browser or proxy cache by
/// default, and an endpoint that wants caching says so. The auth endpoints are <c>no-store</c> whatever they
/// set: token pairs must never be cached.</item>
/// </list>
/// The probes, the OpenAPI document and the Scalar page sit outside <c>/api/</c> and keep their own caching.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var httpContext = (HttpContext)state;
            IHeaderDictionary headers = httpContext.Response.Headers;

            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "frame-ancestors 'none'";

            // Auth: always, whatever the endpoint set. Any other API response: unless the endpoint chose its own.
            PathString path = httpContext.Request.Path;
            if (IsAuthEndpoint(path)
                || (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) && !headers.ContainsKey(HeaderNames.CacheControl)))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        }, context);

        return next(context);
    }

    /// <summary>True for <c>/api/{version}/auth/...</c>, whatever the version segment.</summary>
    internal static bool IsAuthEndpoint(PathString path)
    {
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase, out PathString afterApi)
            || afterApi.Value is not { Length: > 1 } rest)
        {
            return false;
        }

        // rest is "/v1/auth/login": skip the version segment, then look for "auth".
        int nextSegment = rest.IndexOf('/', 1);
        return nextSegment > 0
               && new PathString(rest[nextSegment..]).StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase);
    }
}
