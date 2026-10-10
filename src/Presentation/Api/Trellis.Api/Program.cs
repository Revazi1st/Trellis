#if (UseSerilog)
using Serilog;
#endif
using Microsoft.Extensions.Caching.Hybrid;
using Scalar.AspNetCore;
using Trellis.Api.Common;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Hardening;
using Trellis.Api.Common.Http;
using Trellis.Application;
using Trellis.Infrastructure;
using Trellis.Persistence;
using Trellis.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

#if (UseSerilog)
// Serilog replaces the logger factory below and hands every event on to the ILoggerProviders still
// registered (writeToProviders). Clearing first leaves exactly one: the OpenTelemetry provider that
// AddServiceDefaults adds, so OTLP log export keeps working and the console is not written twice.
builder.Logging.ClearProviders();

#endif
// OpenTelemetry for every meter and trace source named Trellis.* (the Invoices business meter, the
// outbox meter and dispatch spans, the use-case spans), plus health probes and resilient HttpClients.
builder.AddServiceDefaults();

#if (UseSerilog)
// Levels come from the "Serilog" section (it mirrors Logging:LogLevel): Serilog does not read Logging.
// preserveStaticLogger: this host keeps its own logger instead of replacing the process-wide Log.Logger,
// which several hosts in one process (the in-process test fixtures) would otherwise write through.
builder.Services.AddSerilog(
    static (services, config) => config
        .ReadFrom.Configuration(services.GetRequiredService<IConfiguration>())
        .Enrich.FromLogContext()
        // Explicit format provider: log output must not vary with the host's locale (CA1305).
        .WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture),
    preserveStaticLogger: true,
    writeToProviders: true);
#endif

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddOutboundIntegrations(builder.Configuration);
builder.Services.AddPersistence(builder.Configuration.GetConnectionString("trellis-db")
    ?? throw new InvalidOperationException("Connection string 'trellis-db' is not configured."));
// AddPersistence already turned on Npgsql's retrying execution strategy, and the outbox claim and the identity
// transactions run inside it, see ADR-0023. The Aspire enrichment therefore adds health checks, tracing and
// metrics only, never a second retry policy on top.
builder.EnrichNpgsqlDbContext<AppDbContext>(static settings => settings.DisableRetry = true);

// The data-retention windows and the outbox knobs are checked at boot too: a zero Retention:SweepInterval
// or Outbox:PollInterval would hot-loop its service. Persistence binds them but cannot run DataAnnotations
// itself, so the host that runs them does.
builder.Services.AddOptions<Trellis.Persistence.Maintenance.RetentionOptions>().ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddOptions<Trellis.Persistence.Outbox.OutboxOptions>().ValidateDataAnnotations().ValidateOnStart();

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

// Drain the transactional outbox in the long-running API host (ADR-0018, ADR-0023), and prune the
// bookkeeping tables (outbox / idempotency keys / refresh tokens) on a timer.
builder.Services.AddOutboxProcessing(builder.Configuration);
builder.Services.AddDataRetention(builder.Configuration);

// HybridCache: in-memory L1 always; a Redis L2 is added below when the UseRedis feature is enabled.
// The write endpoints remove an invoice's entry, but RemoveAsync reaches only THIS instance's L1 (and the
// shared L2): with several API replicas, another replica's L1 keeps serving its copy until it expires. So
// L1 lives 30 seconds, the bound on cross-replica staleness; with UseRedis the shared L2 keeps entries for
// five minutes and a replica refills its L1 from there. Without UseRedis there is no L2: every replica
// caches on its own, for 30 seconds.
builder.Services.AddHybridCache(static cache => cache.DefaultEntryOptions = new HybridCacheEntryOptions
{
    Expiration = TimeSpan.FromMinutes(5),
    LocalCacheExpiration = TimeSpan.FromSeconds(30),
});
#if (UseRedis)
// Aspire registers an IDistributedCache backed by the "cache" Redis resource, which HybridCache uses as its L2.
builder.AddRedisDistributedCache("cache");
#endif

var app = builder.Build();

// First in the pipeline on purpose: every log line after this carries the correlation id, including
// the ones the exception handler writes.
app.UseCorrelationId();

app.UseExceptionHandler();

// A response that would leave with a status and no body (an unknown route's 404, a 405, the request-timeout
// 504, Kestrel's 413) gets the same problem document as everything else; responses that already wrote a
// body — the challenge 401, the 403, the 429, every Result-produced problem — are untouched (ADR-0024).
app.UseStatusCodePages();

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
