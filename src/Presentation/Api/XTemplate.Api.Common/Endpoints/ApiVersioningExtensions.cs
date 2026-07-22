using Asp.Versioning;
using Asp.Versioning.Builder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace XTemplate.Api.Common.Endpoints;

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
            options.DefaultApiVersion = new ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        });

        return services;
    }

    /// <summary>Creates the versioned <c>/api/v{version}</c> group that feature endpoints are mapped onto.</summary>
    public static RouteGroupBuilder MapVersionedApiGroup(this WebApplication app)
    {
        ApiVersionSet versionSet = app.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1, 0))
            .ReportApiVersions()
            .Build();

        return app.MapGroup("/api/v{version:apiVersion}").WithApiVersionSet(versionSet);
    }
}
