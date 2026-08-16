var builder = DistributedApplication.CreateBuilder(args);

// Image major pinned: postgres data volumes do not survive major-version bumps.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18")
    .WithDataVolume()
    .WithPgAdmin();

var database = postgres.AddDatabase("trellis-db", databaseName: "trellis");

var migrations = builder.AddProject<Projects.Trellis_MigrationService>("migrations")
    .WithReference(database)
    .WaitFor(database);

#if (UseRedis)
var cache = builder.AddRedis("cache");
#endif

var api = builder.AddProject<Projects.Trellis_Api>("api")
    .WithReference(database)
#if (UseRedis)
    .WithReference(cache)
    .WaitFor(cache)
#endif
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health");

#if (IncludeUI)
builder.AddProject<Projects.Trellis_UI_Web>("web")
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();
#endif

// 'aspire publish' target: docker-compose artifacts; images built via dotnet publish /t:PublishContainer — no Dockerfiles.
builder.AddDockerComposeEnvironment("compose");

await builder.Build().RunAsync();
