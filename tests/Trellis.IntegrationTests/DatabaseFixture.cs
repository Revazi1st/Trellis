using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Respawn.Graph;
using Trellis.Persistence;
using Trellis.TestSupport.Database;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>One real database server per test collection; migrations applied once, Respawn wipes data between tests.</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private DbConnection? _connection;
    private Respawner? _respawner;

    public string ConnectionString => Server.ConnectionString;

    /// <summary>
    /// The server, for what a test needs from the engine without naming it: options for a context of its
    /// own (<c>OptionsFor</c>, <c>ModelOnlyOptionsFor</c>) and the reading of a unique violation.
    /// </summary>
    internal ITestDatabase Server { get; } = ITestDatabase.Create();

    public async ValueTask InitializeAsync()
    {
        await Server.StartAsync();

        await using (var context = new AppDbContext(Server.OptionsFor<AppDbContext>()
#if (UseMultitenancy)
            , new FixtureTenantContext()
#endif
            ))
        {
            await context.Database.MigrateAsync();
        }

        _connection = await Server.OpenConnectionAsync();

        // Respawn's adapter and the schema it empties are the engine's (docs/switching-database.md).
        _respawner = await Respawner.CreateAsync(_connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table("__EFMigrationsHistory")],
        });
    }

#if (UseMultitenancy)
    private sealed class FixtureTenantContext : Trellis.Application.Abstractions.ITenantContext
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

        await Server.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
