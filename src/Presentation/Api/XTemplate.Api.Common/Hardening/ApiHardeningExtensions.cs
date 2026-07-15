using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XTemplate.Api.Common.Hardening;

/// <summary>Cross-cutting API host hardening: rate limiting, CORS, request timeouts.</summary>
public static class ApiHardeningExtensions
{
    public const string AuthRateLimitPolicy = "auth";
    public const string DefaultCorsPolicy = "default";

    public static IServiceCollection AddApiHardening(this IServiceCollection services, IConfiguration configuration)
    {
        // Rate limiting: a global partition per client + a stricter fixed window for auth endpoints.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ClientKey(context),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 100, Window = TimeSpan.FromMinutes(1) }));

            options.AddPolicy(AuthRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    ClientKey(context),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
        });

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

    /// <summary>Middleware order: timeouts → CORS → rate limiter. Call before mapping endpoints.</summary>
    public static WebApplication UseApiHardening(this WebApplication app)
    {
        app.UseRequestTimeouts();
        app.UseCors(DefaultCorsPolicy);
        app.UseRateLimiter();
        return app;
    }

    private static string ClientKey(HttpContext context)
        => context.User.Identity?.Name
           ?? context.Connection.RemoteIpAddress?.ToString()
           ?? "anonymous";
}
