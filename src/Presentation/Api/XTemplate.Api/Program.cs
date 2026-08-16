#if (UseSerilog)
using Serilog;
#endif
using Scalar.AspNetCore;
using XTemplate.Api.Common;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Hardening;
using XTemplate.Application;
using XTemplate.Application.Observability;
using XTemplate.Application.Options;
using XTemplate.Infrastructure;
using XTemplate.Persistence;
using XTemplate.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

#if (UseSerilog)
builder.Services.AddSerilog(static (services, config) => config
    .ReadFrom.Configuration(services.GetRequiredService<IConfiguration>())
    .Enrich.FromLogContext()
    .WriteTo.Console());
#endif

builder.Services.AddApplication();

// Export the Invoices business meter (counters are incremented by outbox-dispatched event handlers).
builder.Services.AddOpenTelemetry().WithMetrics(static metrics => metrics.AddMeter(InvoiceMetrics.MeterName));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddPersistence(builder.Configuration.GetConnectionString("xtemplate-db")
    ?? throw new InvalidOperationException("Connection string 'xtemplate-db' is not configured."));
builder.EnrichNpgsqlDbContext<AppDbContext>(static settings => settings.DisableRetry = true);

AuthTokenOptions authOptions = builder.Configuration.GetSection(AuthTokenOptions.SectionName).Get<AuthTokenOptions>()
    ?? throw new InvalidOperationException($"Configuration section '{AuthTokenOptions.SectionName}' is missing.");
builder.Services.AddApiAuthentication(authOptions);

builder.Services.AddExceptionHandler<XTemplate.Api.ConcurrencyExceptionHandler>();
builder.Services.AddApiCommon();
builder.Services.AddApiDocumentation();
builder.Services.AddApiVersioningDefaults();
builder.Services.AddApiHardening(builder.Configuration);

// Database readiness check (untagged → part of /health readiness, excluded from /alive liveness).
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database");

// Drain the transactional outbox in the long-running API host (ADR-0018).
builder.Services.AddOutboxProcessing();

// HybridCache: in-memory L1 always; a Redis L2 is added below when the UseRedis feature is enabled.
builder.Services.AddHybridCache();
#if (UseRedis)
// Aspire registers an IDistributedCache backed by the "cache" Redis resource, which HybridCache uses as its L2.
builder.AddRedisDistributedCache("cache");
#endif

var app = builder.Build();

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
