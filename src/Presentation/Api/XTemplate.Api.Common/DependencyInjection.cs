using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using XTemplate.Api.Common.Caching;
using XTemplate.Api.Common.Http;
using XTemplate.Api.Common.OpenApi;
using XTemplate.Application.Abstractions;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Api.Common;

public static class DependencyInjection
{
    /// <summary>Cross-cutting API-host plumbing: problem details + exception handler, current user, typed-ID JSON.</summary>
    public static IServiceCollection AddApiCommon(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
#if (UseMultitenancy)
        services.AddScoped<XTemplate.Application.Abstractions.ITenantContext, TenantContext>();
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
