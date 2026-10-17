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
    .WithExternalHttpEndpoints();
#endif

if (builder.ExecutionContext.IsRunMode)
{
    // Local `aspire run` behaves like F5: Development settings — demo seed data, Scalar, the dev
    // signing key. Publish mode deliberately sets nothing; the deployment environment decides.
    migrations.WithEnvironment("DOTNET_ENVIRONMENT", "Development");
    api.WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");
    // A second dashboard link straight to the API reference; the endpoint's own URL opens a 404.
    api.WithUrlForEndpoint("http", static _ => new() { Url = "/scalar/v1", DisplayText = "Scalar" });
#if (IncludeUI)
    web.WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");
#endif
}

// 'aspire publish' target: docker-compose artifacts; images built via dotnet publish /t:PublishContainer — no Dockerfiles.
builder.AddDockerComposeEnvironment("compose");

await builder.Build().RunAsync();
