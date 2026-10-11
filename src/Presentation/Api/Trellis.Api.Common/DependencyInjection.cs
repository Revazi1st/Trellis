using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Trellis.Api.Common.Caching;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Http;
using Trellis.Api.Common.OpenApi;
using Trellis.Application.Abstractions;
using Trellis.SharedKernel.Domain;

namespace Trellis.Api.Common;

public static class DependencyInjection
{
    /// <summary>Cross-cutting API-host plumbing: problem details (one shape for every 4xx/5xx) + exception handlers, current user, typed-ID JSON.</summary>
    public static IServiceCollection AddApiCommon(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
#if (UseMultitenancy)
        services.AddScoped<Trellis.Application.Abstractions.ITenantContext, TenantContext>();
#endif

        services.AddScoped<CacheKeys>();

        // The single enrichment point (ADR-0024): CustomResults.Problem, the status-code pages, the
        // exception handlers and every WriteProblemAsync caller all write through IProblemDetailsService,
        // so this is where every problem gets its type, instance, code and correlationId.
        services.AddProblemDetails(static options => options.CustomizeProblemDetails = EnrichProblem);

        // Specific first, catch-all last: the middleware asks the handlers in registration order.
        services.AddExceptionHandler<BadHttpRequestExceptionHandler>();
        services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

        // The same answer in every environment. By default a request the framework cannot bind (malformed
        // JSON, /invoices/not-a-guid) throws BadHttpRequestException only in Development and is a BODYLESS
        // 400 everywhere else; always throwing routes both through BadHttpRequestExceptionHandler, which
        // writes the 400/413/415 problem document and logs at Information. The exception-handler
        // middleware's own diagnostics (an Error log line, error.type on the request metric) are
        // suppressed by default for an exception an IExceptionHandler handled, so a malformed request
        // never counts as an incident.
        services.Configure<RouteHandlerOptions>(static options => options.ThrowOnBadRequest = true);

        services.Configure<JsonOptions>(static options =>
            options.SerializerOptions.Converters.Add(new StronglyTypedIdJsonConverterFactory()));

        return services;
    }

    /// <summary>One OpenAPI 3.1 document per API version (ADR-0025: concrete paths, problem responses by shape, typed-ID and decimal schemas, the bearer scheme, idempotency metadata); the UI is mapped per-environment in the host.</summary>
    public static IServiceCollection AddApiDocumentation(this IServiceCollection services)
        => services.AddVersionedOpenApiDocument(ApiVersions.V1);

    /// <summary>
    /// Runs after ASP.NET's defaults (which set <c>status</c>, a generic <c>title</c>/<c>type</c> and
    /// <c>traceId</c>) and before serialisation. Every problem leaves with: <c>code</c> (the authored one,
    /// or <c>Http.{ReasonPhrase}</c> for a response that reached the client without a body — an unknown
    /// route's 404, a 405, the request-timeout 504 — and the catalogue's row for a bodyless 413/415);
    /// <c>type</c> = <c>urn:problem-type:{code}</c>, overwriting the RFC 9110 link the defaults put there;
    /// a human <c>title</c>; <c>instance</c> = the request path; <c>correlationId</c> = the
    /// <c>X-Correlation-ID</c> response header; and on a 401 a <c>WWW-Authenticate</c> header when its
    /// writer set none.
    /// </summary>
    private static void EnrichProblem(ProblemDetailsContext context)
    {
        Microsoft.AspNetCore.Mvc.ProblemDetails problem = context.ProblemDetails;
        HttpContext http = context.HttpContext;

        int status = problem.Status ??= http.Response.StatusCode;
        problem.Instance ??= (http.Request.PathBase + http.Request.Path).Value;

        string code;
        if (problem.Extensions.TryGetValue(ProblemExtensions.Code, out object? value) && value is string authored)
        {
            code = authored;
            problem.Title ??= ProblemTitles.ForStatus(status);
        }
        else
        {
            // Nobody authored this problem (status-code pages): the status is all there is to derive from,
            // and the defaults' RFC reason phrase gives way to the catalogue title.
            ProblemDescriptor fallback = ProblemCatalog.ForStatus(status);
            code = fallback.Code;
            problem.Extensions[ProblemExtensions.Code] = code;
            problem.Title = fallback.Title;
            problem.Detail ??= fallback.Detail;
        }

        problem.Type = ProblemTypes.For(code);

        // RFC 9110 §15.5.2: every 401 names the scheme that would succeed. The JWT challenge sets its own
        // (with error="invalid_token" for a rejected token); a 401 produced from a Result (login's
        // Auth.InvalidCredentials, refresh's Auth.InvalidRefreshToken) gets the plain scheme here.
        if (status == StatusCodes.Status401Unauthorized && !http.Response.Headers.ContainsKey(HeaderNames.WWWAuthenticate))
        {
            http.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
        }

        if (CorrelationIdMiddleware.TryGet(http, out string? correlationId))
        {
            problem.Extensions[ProblemExtensions.CorrelationId] = correlationId;
        }
    }
}
