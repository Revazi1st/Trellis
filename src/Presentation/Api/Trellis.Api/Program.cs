#if (UseSerilog)
using Serilog;
#endif
using Scalar.AspNetCore;
using Trellis.Api.Common;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Hardening;
using Trellis.Application;
using Trellis.Application.Observability;
using Trellis.Application.Options;
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
builder.Services.AddPersistence(builder.Configuration.GetConnectionString("trellis-db")
    ?? throw new InvalidOperationException("Connection string 'trellis-db' is not configured."));
builder.EnrichNpgsqlDbContext<AppDbContext>(static settings => settings.DisableRetry = true);

AuthTokenOptions authOptions = builder.Configuration.GetSection(AuthTokenOptions.SectionName).Get<AuthTokenOptions>()
    ?? throw new InvalidOperationException($"Configuration section '{AuthTokenOptions.SectionName}' is missing.");
if (authOptions.SigningKey.Length < 32)
{
    // Fail at startup, not on the first login: an empty key would otherwise surface as an opaque
    // SymmetricSecurityKey/IDX10653 error deep inside the token stack.
    throw new InvalidOperationException(
        "Authentication:SigningKey is missing or shorter than 32 characters (256 bits for HMAC-SHA256). "
        + "Set it via user secrets in development (dotnet user-secrets set \"Authentication:SigningKey\" \"<random 32+ chars>\") "
        + "or via the environment/secret store in production.");
}

builder.Services.AddApiAuthentication(authOptions);

builder.Services.AddExceptionHandler<Trellis.Api.ConcurrencyExceptionHandler>();
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
