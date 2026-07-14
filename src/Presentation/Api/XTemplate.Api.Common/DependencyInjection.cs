using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddProblemDetails();
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

        services.Configure<JsonOptions>(static options =>
            options.SerializerOptions.Converters.Add(new StronglyTypedIdJsonConverterFactory()));

        return services;
    }

    /// <summary>OpenAPI 3.1 document generation with typed-ID schema support; UI is mapped per-environment in the host.</summary>
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
    {
        services.AddOpenApi(static options => options.AddSchemaTransformer<StronglyTypedIdSchemaTransformer>());
        return services;
    }
}
