using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace Trellis.Api.Common.Hardening;

/// <summary>
/// Cross-cutting API host hardening (ADR-0021): forwarded headers, request-size limits, rate limiting,
/// CORS, request timeouts and security response headers.
/// </summary>
public static class ApiHardeningExtensions
{
    public const string AuthRateLimitPolicy = "auth";
    public const string DefaultCorsPolicy = "default";

    /// <summary>Configuration key (Kestrel's own) for the request body cap, in bytes.</summary>
    public const string MaxRequestBodySizeKey = "Kestrel:Limits:MaxRequestBodySize";

    /// <summary>Configuration key (Kestrel's own) for the total request-header cap, in bytes.</summary>
    public const string MaxRequestHeadersTotalSizeKey = "Kestrel:Limits:MaxRequestHeadersTotalSize";

    /// <summary>1 MB: generous for a JSON API whose largest request is well under a kilobyte (Kestrel's default is ~28.6 MB).</summary>
    public const long DefaultMaxRequestBodySize = 1024 * 1024;

    /// <summary>32 KB of request headers.</summary>
    public const int DefaultMaxRequestHeadersTotalSize = 32 * 1024;

    /// <summary>
    /// The host-configuration key behind <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>. When it is
    /// "true" the WebHost clears both trusted-proxy lists and inserts its own forwarded-headers
    /// middleware at the front of the pipeline (the same comparison the framework makes).
    /// </summary>
    private const string ForwardedHeadersEnabledKey = "ForwardedHeaders_Enabled";

    public static IServiceCollection AddApiHardening(this IServiceCollection services, IConfiguration configuration)
    {
        AddForwardedHeadersTrust(services, configuration);

        // Request-size limits. Kestrel only (TestServer and IIS ignore them); the keys are Kestrel's own,
        // so an operator who already knows Kestrel configuration finds them where expected.
        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize =
                configuration.GetValue<long?>(MaxRequestBodySizeKey) ?? DefaultMaxRequestBodySize;
            options.Limits.MaxRequestHeadersTotalSize =
                configuration.GetValue<int?>(MaxRequestHeadersTotalSizeKey) ?? DefaultMaxRequestHeadersTotalSize;
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

    private static void AddForwardedHeadersTrust(IServiceCollection services, IConfiguration configuration)
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

        // Trust never widens. ASPNETCORE_FORWARDEDHEADERS_ENABLED (set by Aspire's publishers) makes
        // the WebHost clear both trusted lists before the Configure above runs, and with both lists
        // empty ForwardedHeadersMiddleware skips the proxy check: every caller could pick its own
        // X-Forwarded-For and with it its own rate-limit partition. Restore loopback-only trust
        // instead (never fail the boot: publish output must still start); UseApiHardening logs a
        // warning naming the setting to configure.
        services.PostConfigure<ForwardedHeadersOptions>(static options =>
        {
            if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
            {
                options.KnownProxies.Add(IPAddress.IPv6Loopback);
                options.KnownProxies.Add(IPAddress.Loopback);
            }
        });
    }

    /// <summary>Early middleware: forwarded headers → security headers → timeouts → CORS. Call before authentication.</summary>
    public static WebApplication UseApiHardening(this WebApplication app)
    {
        if (IsForwardedHeadersEnabledByHost(app.Configuration))
        {
            // The WebHost's startup filter already runs UseForwardedHeaders first in the pipeline. A
            // second pass would process one more X-Forwarded-For entry (effectively doubling
            // ForwardLimit), and that entry may be one the client wrote itself.
            WarnIfForwardingTrustsLoopbackOnly(app);
        }
        else
        {
            app.UseForwardedHeaders();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
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

    private static bool IsForwardedHeadersEnabledByHost(IConfiguration configuration)
        => string.Equals(configuration[ForwardedHeadersEnabledKey], "true", StringComparison.OrdinalIgnoreCase);

    private static void WarnIfForwardingTrustsLoopbackOnly(WebApplication app)
    {
        ForwardedHeadersOptions options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        bool loopbackOnly = options.KnownProxies.All(IPAddress.IsLoopback)
                            && options.KnownIPNetworks.All(static network => IPAddress.IsLoopback(network.BaseAddress));
        if (!loopbackOnly)
        {
            return;
        }

        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(ApiHardeningExtensions))
            .LogWarning(
                "ASPNETCORE_FORWARDEDHEADERS_ENABLED is set but no trusted proxy is configured, so X-Forwarded-For "
                + "is honoured from loopback only and every caller behind the ingress shares its rate-limit partition. "
                + "Set ForwardedHeaders__KnownNetworks__0 (CIDR) or ForwardedHeaders__KnownProxies__0 (IP) to the "
                + "ingress/proxy address so rate limits key on the real client address.");
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

        // For endpoints that accept a GUESSABLE credential (login, register). Endpoints that accept an
        // unguessable token (refresh, logout) stay under the global limiter only: throttling a 256-bit
        // random value per source buys nothing, and a shared source (a Blazor Server host) would turn
        // ordinary silent refreshes into 429s (ADR-0021).
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
