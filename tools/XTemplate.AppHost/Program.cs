var builder = DistributedApplication.CreateBuilder(args);

// Image major pinned: postgres data volumes do not survive major-version bumps.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18")
    .WithDataVolume()
    .WithPgAdmin();

var database = postgres.AddDatabase("xtemplate-db", databaseName: "xtemplate");

var migrations = builder.AddProject<Projects.XTemplate_MigrationService>("migrations")
    .WithReference(database)
    .WaitFor(database);

var api = builder.AddProject<Projects.XTemplate_Api>("api")
    .WithReference(database)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/health");

#if (IncludeUI)
builder.AddProject<Projects.XTemplate_UI_Web>("web")
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();
#endif

// 'aspire publish' target: docker-compose artifacts; images built via dotnet publish /t:PublishContainer — no Dockerfiles.
builder.AddDockerComposeEnvironment("compose");

await builder.Build().RunAsync();
