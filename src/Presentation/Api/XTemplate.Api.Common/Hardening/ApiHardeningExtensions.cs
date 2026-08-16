using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using IPNetwork = System.Net.IPNetwork;

namespace XTemplate.Api.Common.Hardening;

/// <summary>Cross-cutting API host hardening: forwarded headers, rate limiting, CORS, request timeouts.</summary>
public static class ApiHardeningExtensions
{
    public const string AuthRateLimitPolicy = "auth";
    public const string DefaultCorsPolicy = "default";

    public static IServiceCollection AddApiHardening(this IServiceCollection services, IConfiguration configuration)
    {
        // Behind a proxy/ingress, RemoteIpAddress is the proxy — every caller would share one rate-limit
        // partition. Honour X-Forwarded-For, but only from proxies explicitly trusted via the
        // ForwardedHeaders configuration section: KnownProxies takes IP addresses and KnownNetworks
        // takes CIDR ranges. The default trusts loopback only, so untrusted callers cannot spoof.
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            foreach (string proxy in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (string network in configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
            {
                options.KnownIPNetworks.Add(IPNetwork.Parse(network));
            }
        });

        // Rate limiting: a global partition per client + a stricter fixed window for auth endpoints.
        services.AddRateLimiter(ConfigureRateLimiter);

        // CORS: allowed origins come from configuration ("Cors:AllowedOrigins"); none by default (same-origin only).
        string[] allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(options => options.AddPolicy(DefaultCorsPolicy, policy =>
        {
            if (allowedOrigins.Length > 0)
            {
                policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
            }
        }));

        // Request timeouts: a default cap so slow/hung requests are shed.
        services.AddRequestTimeouts(options =>
            options.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(30),
                TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
            });

        return services;
    }

    /// <summary>Early middleware: forwarded headers → timeouts → CORS. Call before authentication.</summary>
    public static WebApplication UseApiHardening(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseRequestTimeouts();
        app.UseCors(DefaultCorsPolicy);
        return app;
    }

    /// <summary>
    /// Rate limiting partitions on the authenticated principal, so this must run AFTER
    /// <c>UseAuthentication()</c> — any earlier and every request is still anonymous, collapsing
    /// authenticated traffic into shared per-IP buckets.
    /// </summary>
    public static WebApplication UseApiRateLimiting(this WebApplication app)
    {
        app.UseRateLimiter();
        return app;
    }

    private static void ConfigureRateLimiter(Microsoft.AspNetCore.RateLimiting.RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // A bodyless 429 leaves clients guessing; RFC 9457 + Retry-After lets them back off correctly.
        options.OnRejected = static async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsJsonAsync(
                new Microsoft.AspNetCore.Mvc.ProblemDetails
                {
                    Title = "Too many requests",
                    Detail = "Rate limit exceeded. Retry after the interval in the Retry-After header.",
                    Status = StatusCodes.Status429TooManyRequests,
                },
                options: null,
                contentType: "application/problem+json",
                cancellationToken);
        };

        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromMinutes(1) }));

        options.AddPolicy(AuthRateLimitPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    }

    /// <summary>Per authenticated user (stable id claim), else per client IP (X-Forwarded-For aware).</summary>
    private static string ClientKey(HttpContext context)
        => context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
           ?? context.Connection.RemoteIpAddress?.ToString()
           ?? "anonymous";
}
