#if (UseSerilog)
using Serilog;
#endif
using Scalar.AspNetCore;
using Trellis.Api.Common;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Hardening;
using Trellis.Api.Common.Http;
using Trellis.Application;
using Trellis.Application.Observability;
using Trellis.Infrastructure;
using Trellis.Persistence;
using Trellis.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

#if (UseSerilog)
builder.Services.AddSerilog(static (services, config) => config
    .ReadFrom.Configuration(services.GetRequiredService<IConfiguration>())
    .Enrich.FromLogContext()
    // Explicit format provider: log output must not vary with the host's locale (CA1305).
    .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture));
#endif

builder.Services.AddApplication();

// Export the Invoices business meter (counters are incremented by outbox-dispatched event handlers)
// and the outbox meter + trace source (dispatch spans rejoin the originating request's trace).
builder.Services.AddOpenTelemetry()
    .WithMetrics(static metrics => metrics.AddMeter(InvoiceMetrics.MeterName, Trellis.Persistence.Outbox.OutboxMetrics.MeterName))
    .WithTracing(static tracing => tracing.AddSource(
        Trellis.Persistence.Outbox.OutboxMetrics.ActivitySourceName,
        Trellis.SharedKernel.Decorators.TracingDecorators.ActivitySourceName));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOutboundIntegrations(builder.Configuration);
builder.Services.AddPersistence(builder.Configuration.GetConnectionString("trellis-db")
    ?? throw new InvalidOperationException("Connection string 'trellis-db' is not configured."));
builder.EnrichNpgsqlDbContext<AppDbContext>(static settings => settings.DisableRetry = true);

// This host mints and validates tokens, so a missing or too-short signing key must stop it at boot
// with an actionable message rather than fail the first login. AddApiAuthentication then reads those
// same validated options — the Authentication section is never bound a second time.
builder.Services.RequireValidTokenOptions();
builder.Services.AddApiAuthentication();

builder.Services.AddApiCommon();
builder.Services.AddApiDocumentation();
builder.Services.AddApiVersioningDefaults();
builder.Services.AddApiHardening(builder.Configuration);

// Database readiness check (untagged → part of /health readiness, excluded from /alive liveness).
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

// Drain the transactional outbox in the long-running API host (ADR-0018), and prune the
// bookkeeping tables (outbox / idempotency keys / refresh tokens) on a timer.
builder.Services.AddOutboxProcessing();
builder.Services.AddDataRetention(builder.Configuration);

// HybridCache: in-memory L1 always; a Redis L2 is added below when the UseRedis feature is enabled.
builder.Services.AddHybridCache();
#if (UseRedis)
// Aspire registers an IDistributedCache backed by the "cache" Redis resource, which HybridCache uses as its L2.
builder.AddRedisDistributedCache("cache");
#endif

var app = builder.Build();

// First in the pipeline on purpose: every log line after this carries the correlation id, including
// the ones the exception handler writes.
app.UseCorrelationId();

app.UseExceptionHandler();

app.UseApiHardening();

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseAuthentication();
app.UseApiRateLimiting();   // after authentication so partitions key on the real user, not "anonymous"
app.UseAuthorization();

app.MapVersionedApiGroup().MapEndpoints(typeof(Program).Assembly);

await app.RunAsync();

/// <summary>Marker for endpoint discovery and WebApplicationFactory-style test hosts.</summary>
public partial class Program
{
    protected Program()
    {
    }
}
