using Trellis.ServiceDefaults;

var builder = DistributedApplication.CreateBuilder(args);

// Image major pinned: postgres data volumes do not survive major-version bumps.
// Persistent lifetime: the container outlives the AppHost, so the next run finds PostgreSQL already started and
// healthy instead of waiting for a new one; the data volume keeps the data either way. pgAdmin stays
// session-scoped: nothing waits for it. Removing the container and the volume gives a fresh database
// (docs/development.md). ASPIREPERSISTENCE001: experimental in Aspire 13.6 and kept on purpose, it is the call
// the API docs prefer over the older WithLifetime(ContainerLifetime.Persistent).
#pragma warning disable ASPIREPERSISTENCE001
var postgres = builder.AddPostgres(ServiceNames.Postgres)
    .WithImageTag("18")
    .WithDataVolume()
    .WithPersistentLifetime()
    .WithPgAdmin();
#pragma warning restore ASPIREPERSISTENCE001

var database = postgres.AddDatabase(ServiceNames.Database, databaseName: "trellis");

// Launch profiles are EXCLUDED for Aspire-launched resources on purpose: the IDE profiles carry a
// hardcoded dev connection string as a command-line ARG (for the dev-container flow), and args
// outrank the connection string Aspire injects — every resource would silently point at the
// compose database instead of the provisioned one. Aspire assigns endpoints itself.
var migrations = builder.AddProject<Projects.Trellis_MigrationService>(ServiceNames.Migrations, static options => options.ExcludeLaunchProfile = true)
    .WithReference(database)
    .WaitFor(database);

#if (UseRedis)
// RedisInsight beside the cache, as pgAdmin sits beside PostgreSQL: a dashboard tile to look at the keys.
var cache = builder.AddRedis(ServiceNames.Cache)
    .WithRedisInsight();
#endif

var api = builder.AddProject<Projects.Trellis_Api>(ServiceNames.Api, static options => options.ExcludeLaunchProfile = true)
    // With no launch profile, endpoints must be declared here; Aspire assigns the port.
    .WithHttpEndpoint()
    // The API is a public surface with or without the UI, so 'aspire publish' must expose it: without
    // this the compose output only 'expose:'s it on the internal network (docs/deployment.md).
    .WithExternalHttpEndpoints()
    .WithReference(database)
#if (UseRedis)
    .WithReference(cache)
    .WaitFor(cache)
#endif
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health");

#if (IncludeUI)
var web = builder.AddProject<Projects.Trellis_UI_Web>(ServiceNames.Web, static options => options.ExcludeLaunchProfile = true)
    .WithHttpEndpoint()
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints()
    // Healthy once its own /health answers, as for the API: without a check, healthy means the process started,
    // so whatever waits for web would wait for a host that may not serve a page yet.
    .WithHttpHealthCheck("/health");
#endif

if (builder.ExecutionContext.IsRunMode)
{
    // Local `aspire run` behaves like F5: Development settings — demo seed data, Scalar, the dev
    // signing key. Publish mode sets no environment; the deployment decides.
    migrations.WithEnvironment("DOTNET_ENVIRONMENT", "Development");
    api.WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");
    // A second dashboard link, named for what it opens: the API reference. The endpoint's own URL lands there too,
    // through the API's Development-only root redirect.
    api.WithUrlForEndpoint("http", static _ => new() { Url = "/scalar/v1", DisplayText = "Scalar" });
#if (IncludeUI)
    web.WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");
#endif
}

if (builder.ExecutionContext.IsPublishMode)
{
    // The two secrets a deployment must supply, as parameters: the compose file reads them from .env
    // (SIGNING_KEY, SEED_ADMIN_PASSWORD) instead of leaving them to hand edits of the YAML, and 'aspire publish'
    // and 'aspire deploy' ask for them unless the AppHost's configuration holds them (Parameters:signing-key,
    // Parameters:seed-admin-password). Run mode declares neither: F5 uses the Development key and password.
    var signingKey = builder.AddParameter("signing-key", secret: true);
    var seedAdminPassword = builder.AddParameter("seed-admin-password", secret: true);
    api.WithEnvironment("Authentication__SigningKey", signingKey);
    migrations.WithEnvironment("Seed__AdminPassword", seedAdminPassword);

    // The long-running services come back after a crash or a reboot of the host. The migration job keeps no
    // restart policy: it runs once per 'docker compose up'.
    postgres.PublishAsDockerComposeService(static (_, service) => service.Restart = "unless-stopped");
    api.PublishAsDockerComposeService(static (_, service) => service.Restart = "unless-stopped");
#if (UseRedis)
    cache.PublishAsDockerComposeService(static (_, service) => service.Restart = "unless-stopped");
#endif
#if (IncludeUI)
    web.PublishAsDockerComposeService(static (_, service) => service.Restart = "unless-stopped");
#endif
}

// 'aspire publish' target: docker-compose artifacts; images built via dotnet publish /t:PublishContainer — no Dockerfiles.
// The Aspire dashboard shows every log line and trace, so its UI is published on the host's loopback only and
// reached over an SSH tunnel (docs/deployment.md); its OTLP ports stay on the compose network.
builder.AddDockerComposeEnvironment("compose")
    .WithDashboard(static dashboard => dashboard
        .WithHostPort(18888)
        .PublishAsDockerComposeService(static (_, service) =>
            service.Ports = [.. service.Ports.Select(static port => $"127.0.0.1:{port}")]));

await builder.Build().RunAsync();
