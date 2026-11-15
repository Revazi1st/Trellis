using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Trellis.TestSupport.Database;

/// <summary>
/// The database server the tests run against, and what a test may ask of its engine without naming it.
/// IntegrationTests' <c>DatabaseFixture</c> and ApiTests' <c>ApiFactory</c> each hold one; a test class
/// names neither the provider nor its exception types. <see cref="Create"/> is the one line that chooses
/// the engine, and <see cref="PostgresTestDatabase"/> the one file that starts the server and configures
/// the provider: a project that moves to another engine writes its own implementation of these members
/// and changes that line (<c>docs/switching-database.md</c>). One source file, compiled into
/// IntegrationTests and ApiTests (each csproj links the folder).
/// </summary>
internal interface ITestDatabase : IAsyncDisposable
{
    /// <summary>The engine the tests run on. <paramref name="databaseName"/>: the database to create, or the engine's default.</summary>
    static ITestDatabase Create(string? databaseName = null) => new PostgresTestDatabase(databaseName);

    /// <summary>What a host is given: <c>AddPersistence(connectionString)</c>, <c>ConnectionStrings:&lt;name&gt;</c>. Valid once started.</summary>
    string ConnectionString { get; }

    /// <summary>Starts the server.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The provider over this server for a context a test builds itself: no retrying strategy and none
    /// of the host's interceptors. <paramref name="database"/> names another database on the same server,
    /// which the test creates and deletes with <c>EnsureCreatedAsync</c> and <c>EnsureDeletedAsync</c>.
    /// </summary>
    void Configure(DbContextOptionsBuilder options, string? database = null);

    /// <summary>The provider for a context whose model is built and that never connects.</summary>
    void ConfigureWithoutServer(DbContextOptionsBuilder options);

    /// <summary>An open connection of its own, for the tool that resets the data between tests.</summary>
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>The name of the unique constraint a failed save violated, or null when it failed for another reason.</summary>
    string? ViolatedUniqueConstraint(DbUpdateException exception);

    /// <summary><see cref="Configure"/> as finished options.</summary>
    DbContextOptions<TContext> OptionsFor<TContext>(string? database = null)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>();
        Configure(options, database);
        return options.Options;
    }

    /// <summary><see cref="ConfigureWithoutServer"/> as finished options.</summary>
    DbContextOptions<TContext> ModelOnlyOptionsFor<TContext>()
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>();
        ConfigureWithoutServer(options);
        return options.Options;
    }
}
