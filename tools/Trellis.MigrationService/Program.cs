using Microsoft.Extensions.Options;
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

// Validated at boot, like every options type: an e-mail typo, a short password or an over-long tenant id
// stops the worker here, naming the key, instead of seeding an admin nobody can sign in as. The worker
// takes the plain POCO.
builder.Services.AddOptions<SeedOptions>()
    .Bind(builder.Configuration.GetSection(SeedOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton(static provider => provider.GetRequiredService<IOptions<SeedOptions>>().Value);
builder.Services.AddHostedService<MigrationWorker>();

try
{
    await builder.Build().RunAsync();
}
catch (OptionsValidationException)
{
    // The host has already logged it ("Hosting failed to start", naming the key). Exit 1 like any other
    // failed run — the API waits on this exit code — instead of crashing on an unhandled exception.
    Environment.ExitCode = 1;
}
