using Asp.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Trellis.Api.Common.Endpoints;

namespace Trellis.Api.Common.OpenApi;

public static class OpenApiDocumentExtensions
{
    /// <summary>
    /// One OpenAPI document per URL-segment API version (ADR-0025): <c>/openapi/v1.json</c> describes
    /// exactly the operations mapped to v1, with concrete <c>/api/v1/…</c> paths and no <c>servers</c>.
    /// Anything outside the versioned group (probes, the reference UI) is excluded by construction.
    /// Adding a v2 is <c>HasApiVersion(V2)</c> on the group plus one more call here.
    /// </summary>
    public static IServiceCollection AddVersionedOpenApiDocument(this IServiceCollection services, ApiVersion version)
    {
        services.AddOpenApi(ApiVersions.DocumentName(version), options =>
        {
            options.ShouldInclude = description => description.ActionDescriptor.EndpointMetadata
                .OfType<ApiVersionMetadata>()
                .Any(metadata => metadata.IsMappedTo(version));

            options.AddDocumentTransformer(new VersionedDocumentTransformer(version));
            options.AddDocumentTransformer<SecuritySchemeTransformer>();
            options.AddSchemaTransformer<StronglyTypedIdSchemaTransformer>();
            options.AddSchemaTransformer<DecimalSchemaTransformer>();
            options.AddOperationTransformer<SecurityRequirementOperationTransformer>();
            options.AddOperationTransformer<IdempotencyOperationTransformer>();
            // Last on purpose: it gives the 401/403/409 entries the two above create their problem body.
            options.AddOperationTransformer<ProblemResponsesOperationTransformer>();
        });

        return services;
    }
}
