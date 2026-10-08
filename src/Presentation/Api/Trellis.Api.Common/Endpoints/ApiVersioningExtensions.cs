using Asp.Versioning;
using Asp.Versioning.Builder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Trellis.Api.Common.Endpoints;

/// <summary>
/// URL-segment API versioning. Routes stay <c>/api/v1/...</c>; the version is now a real, negotiable
/// concept (reported via the <c>api-supported-versions</c> header) so a v2 is additive.
/// </summary>
public static class ApiVersioningExtensions
{
    public static IServiceCollection AddApiVersioningDefaults(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = ApiVersions.V1;
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });

        return services;
    }

    /// <summary>
    /// Creates the versioned <c>/api/v{version}</c> group that feature endpoints are mapped onto.
    /// The group is the API boundary, so it is deny-by-default (ADR-0021): every endpoint on it
    /// requires an authenticated caller and declares its permission with <c>RequirePermission</c>.
    /// An endpoint that must be reachable without a token opts out with <c>AllowAnonymous()</c>
    /// (which wins over group metadata) and is listed in <c>EndpointMetadataTests</c>' allowlist.
    /// Probes and API docs are mapped on the host, never on this group, so they need no exemption.
    /// That is also why there is no fallback policy: it would silently govern whatever a consumer
    /// maps on the host tomorrow.
    /// </summary>
    public static RouteGroupBuilder MapVersionedApiGroup(this WebApplication app)
    {
        ApiVersionSet versionSet = app.NewApiVersionSet()
            .HasApiVersion(ApiVersions.V1)
            .ReportApiVersions()
            .Build();

        return app.MapGroup("/api/v{version:apiVersion}")
            .WithApiVersionSet(versionSet)
            .RequireAuthorization();
    }
}
