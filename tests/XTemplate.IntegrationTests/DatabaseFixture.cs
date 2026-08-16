using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Respawn.Graph;
using Testcontainers.PostgreSql;
using XTemplate.Persistence;
using Xunit;

namespace XTemplate.IntegrationTests;

/// <summary>One real PostgreSQL container per test collection; migrations applied once, Respawn wipes data between tests.</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    // Deliberately NOT WithReuse(true): reuse is experimental and the Ryuk reaper kills "reusable"
    // containers when a parallel test assembly's session ends, failing whoever is still connected.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    private NpgsqlConnection? _connection;
    private Respawner? _respawner;

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        await using (var context = new AppDbContext(options
#if (UseMultitenancy)
            , new FixtureTenantContext()
#endif
            ))
        {
            await context.Database.MigrateAsync();
        }

        _connection = new NpgsqlConnection(ConnectionString);
        await _connection.OpenAsync();

        _respawner = await Respawner.CreateAsync(_connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table("__EFMigrationsHistory")],
        });
    }

#if (UseMultitenancy)
    private sealed class FixtureTenantContext : XTemplate.Application.Abstractions.ITenantContext
    {
        public string? TenantId => null;
    }
#endif

    public async Task ResetAsync() => await _respawner!.ResetAsync(_connection!);

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
