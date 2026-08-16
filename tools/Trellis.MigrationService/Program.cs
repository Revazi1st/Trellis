using Trellis.Application;
using Trellis.Infrastructure;
using Trellis.MigrationService;
using Trellis.Persistence;
using Trellis.ServiceDefaults;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSingleton<Trellis.Application.Abstractions.ICurrentUser, SystemCurrentUser>();
#if (UseMultitenancy)
// AppDbContext and the TenantInterceptor require an ITenantContext; outside an HTTP request there is no tenant.
builder.Services.AddSingleton<Trellis.Application.Abstractions.ITenantContext, SystemTenantContext>();
#endif
builder.Services.AddPersistence(builder.Configuration.GetConnectionString("trellis-db")
    ?? throw new InvalidOperationException("Connection string 'trellis-db' is not configured."));
builder.EnrichNpgsqlDbContext<AppDbContext>(static settings => settings.DisableRetry = true);

builder.Services.AddOpenTelemetry()
    .WithTracing(static tracing => tracing.AddSource(MigrationWorker.ActivitySourceName));

builder.Services.AddSingleton(
    builder.Configuration.GetSection(SeedOptions.SectionName).Get<SeedOptions>() ?? new SeedOptions());
builder.Services.AddHostedService<MigrationWorker>();

await builder.Build().RunAsync();
