using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Trellis.TestSupport.Database;

/// <summary>
/// PostgreSQL in a Testcontainers container: the one test file that starts the server and configures the
/// provider. The other test files that name the engine are on <c>DatabaseEngineTests</c>' allowlist, each
/// with its reason.
/// </summary>
internal sealed class PostgresTestDatabase(string? databaseName) : ITestDatabase
{
    // One container per fixture, deliberately NOT WithReuse(true): reuse is experimental and the Ryuk
    // reaper kills "reusable" containers when a parallel test assembly's session ends, failing whoever is
    // still connected. Debian postgres:18, the image the AppHost, docker-compose.yml and a deployment run:
    // the Alpine image's musl libc collates text differently, so an ORDER BY on text could pass here and
    // sort otherwise there.
    private readonly PostgreSqlContainer _container = Build(databaseName);

    public string ConnectionString => _container.GetConnectionString();

    public Task StartAsync(CancellationToken cancellationToken = default) => _container.StartAsync(cancellationToken);

    public void Configure(DbContextOptionsBuilder options, string? database = null)
        => options.UseNpgsql(database is null
            ? ConnectionString
            : new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString);

    public void ConfigureWithoutServer(DbContextOptionsBuilder options) => options.UseNpgsql("Host=model-only.invalid");

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public string? ViolatedUniqueConstraint(DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation
            ? violation.ConstraintName
            : null;

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    private static PostgreSqlContainer Build(string? databaseName)
    {
        var builder = new PostgreSqlBuilder("postgres:18");
        return (databaseName is null ? builder : builder.WithDatabase(databaseName)).Build();
    }
}
