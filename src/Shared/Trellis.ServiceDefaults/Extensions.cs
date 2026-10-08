using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Trellis.ServiceDefaults;

/// <summary>
/// Cross-service defaults for every host in the solution: OpenTelemetry, health endpoints,
/// service discovery and resilient HttpClients. Works with or without the Aspire AppHost.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// The first segment of every first-party <c>Meter</c> and <c>ActivitySource</c> name
    /// (<c>Trellis.Invoices</c>, <c>Trellis.Outbox</c>, <c>Trellis.UseCases</c>, …). Every host subscribes
    /// to <see cref="TelemetryWildcard"/>, so a new meter or source is exported the moment it exists, with
    /// no per-host list to forget. Its lower-case form (<c>trellis.</c>) prefixes instrument names and custom
    /// attribute names. <c>TelemetryNamingTests</c> holds every name in the solution to it.
    /// </summary>
    public const string TelemetryPrefix = "Trellis";

    /// <summary>The subscription pattern for <see cref="TelemetryPrefix"/>: OpenTelemetry matches <c>*</c> as a wildcard.</summary>
    public const string TelemetryWildcard = TelemetryPrefix + ".*";

    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(static http =>
        {
            // Retries only for safe methods (GET, HEAD, OPTIONS, TRACE). A POST/PUT/PATCH/DELETE whose
            // response was lost may already have taken effect, so retrying it blind can apply it twice (a
            // second invoice, a second payment) and turn the caller's own Idempotency-Key into a 409. A
            // client whose endpoint is proven idempotent replaces this handler with its own (ADR-0020).
            http.AddStandardResilienceHandler(static resilience => resilience.Retry.DisableForUnsafeHttpMethods());
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(static logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => DescribeService(resource, builder))
            .WithMetrics(static metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(TelemetryWildcard)
                // Polly: every HttpClient pipeline's retries, timeouts and open circuits (otherwise an open
                // circuit shows only as failed requests). EF Core: SaveChanges, optimistic-concurrency
                // failures, execution-strategy retries. A meter a host never creates costs nothing.
                .AddMeter("Polly", "Microsoft.EntityFrameworkCore"))
            .WithTracing(static tracing => tracing
                .AddSource(TelemetryWildcard)
                .AddAspNetCoreInstrumentation(static t =>
                    t.Filter = static context =>
                        !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath, StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// Says who emits every signal: <c>service.name</c>, <c>service.version</c> and
    /// <c>deployment.environment.name</c>. Aspire (and any OpenTelemetry-aware deployer) names the service
    /// through <c>OTEL_SERVICE_NAME</c>, which the SDK has already applied and which <c>AddService</c> would
    /// overwrite, so the application name is only the fallback; without it a host outside Aspire reports
    /// as <c>unknown_service:dotnet</c>. A key the deployer sets in <c>OTEL_RESOURCE_ATTRIBUTES</c> wins
    /// over the value computed here, for the same reason.
    /// </summary>
    private static void DescribeService(ResourceBuilder resource, IHostApplicationBuilder builder)
    {
        IConfiguration configuration = builder.Configuration;
        if (string.IsNullOrWhiteSpace(configuration["OTEL_SERVICE_NAME"]))
        {
            resource.AddService(builder.Environment.ApplicationName);
        }

        var setByDeployer = new HashSet<string>(
            (configuration["OTEL_RESOURCE_ATTRIBUTES"] ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static pair => pair.Split('=', 2)[0].Trim()),
            StringComparer.Ordinal);

        KeyValuePair<string, object>[] attributes =
        [
            KeyValuePair.Create<string, object>("service.version", ServiceVersion()),
            KeyValuePair.Create<string, object>("deployment.environment.name", builder.Environment.EnvironmentName),
        ];
        resource.AddAttributes(attributes.Where(attribute => !setByDeployer.Contains(attribute.Key)));
    }

    /// <summary>The host's informational version (with the commit, when the build stamps one), else its assembly version.</summary>
    private static string ServiceVersion()
    {
        Assembly host = Assembly.GetEntryAssembly() ?? typeof(Extensions).Assembly;
        return host.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? host.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", static () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Anonymous probe endpoints, mapped in every environment: orchestrator health checks (the
    /// AppHost's WithHttpHealthCheck, compose/k8s probes) must reach them without credentials.
    /// Keep them internal — do not route /health or /alive through the public ingress; the
    /// response writer leaks only the overall status text either way.
    /// <para>
    /// Both are exempt from rate limiting (ADR-0021): probes arrive from one orchestrator address,
    /// and a 429 on a probe is read as "unhealthy", so an API under load would be pulled out of
    /// rotation or restarted by its own limiter. They are deliberately not <c>ShortCircuit()</c>ed:
    /// routing runs implicitly at the front of the pipeline, so short-circuiting would also skip
    /// the correlation-id and exception-handling middleware the hosts add after it.
    /// </para>
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Readiness: all checks (incl. the database). Liveness: only checks tagged "live".
        app.MapHealthChecks(HealthEndpointPath)
            .DisableRateLimiting();
        app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = static registration => registration.Tags.Contains("live"),
        })
            .DisableRateLimiting();

        return app;
    }
}
