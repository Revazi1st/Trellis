using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Trellis.Api.Common.Caching;
using Trellis.Api.Common.Http;
using Trellis.Api.Common.OpenApi;
using Trellis.Application.Abstractions;
using Trellis.SharedKernel.Domain;

namespace Trellis.Api.Common;

public static class DependencyInjection
{
    /// <summary>Cross-cutting API-host plumbing: problem details + exception handler, current user, typed-ID JSON.</summary>
    public static IServiceCollection AddApiCommon(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
#if (UseMultitenancy)
        services.AddScoped<Trellis.Application.Abstractions.ITenantContext, TenantContext>();
#endif

        services.AddScoped<CacheKeys>();

        services.AddProblemDetails();
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

        services.Configure<JsonOptions>(static options =>
            options.SerializerOptions.Converters.Add(new StronglyTypedIdJsonConverterFactory()));

        return services;
    }

    /// <summary>OpenAPI 3.1 document generation with typed-ID schemas, the bearer security scheme and idempotency metadata; UI is mapped per-environment in the host.</summary>
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(static options =>
        {
            options.AddSchemaTransformer<StronglyTypedIdSchemaTransformer>();
            options.AddDocumentTransformer<SecuritySchemeTransformer>();
            options.AddOperationTransformer<SecurityRequirementOperationTransformer>();
            options.AddOperationTransformer<IdempotencyOperationTransformer>();
        });
        return services;
    }
}
