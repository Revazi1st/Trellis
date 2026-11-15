using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Npgsql;
using Trellis.Persistence.Outbox;

namespace Trellis.Persistence;

/// <summary>
/// Everything in this project that only PostgreSQL understands, in one file (ADR-0033): the provider and
/// its retrying strategy, the concurrency token, the outbox payload's column type, the outbox index's
/// filter, the claim query and the reading of a unique violation. The rest of the project names neither
/// the provider nor a store type, and writes no SQL: each place calls the member below that it needs.
/// The generated migrations are the engine's too.
/// <para>
/// Not an abstraction: nothing implements or replaces these members at run time. A project that moves to
/// another relational engine rewrites this file member by member and generates its migrations again
/// (<c>docs/switching-database.md</c>).
/// </para>
/// </summary>
internal static class PostgresDialect
{
    /// <summary>
    /// The provider over the server at <paramref name="connectionString"/>, with the retrying execution
    /// strategy every host runs under: the outbox claim and the identity transactions run inside
    /// <c>CreateExecutionStrategy()</c> because of it (ADR-0023).
    /// </summary>
    public static void UseServer(DbContextOptionsBuilder options, string connectionString)
        => options.UseNpgsql(connectionString, static npgsql => npgsql.EnableRetryOnFailure());

    /// <summary>
    /// The provider alone, without the retrying strategy, for the context <c>dotnet ef</c> builds
    /// (<see cref="AppDbContextFactory"/>). Without a connection string it names a throwaway local server:
    /// scaffolding a migration reads the model and never connects.
    /// </summary>
    [SuppressMessage("Security", "S2068:Hard-coded credentials are security-sensitive",
        Justification = "Design-time-only throwaway localhost credentials; migrations never run against real databases from here.")]
    public static void UseDesignTime(DbContextOptionsBuilder options, string? connectionString)
        => options.UseNpgsql(connectionString ?? "Host=localhost;Database=trellis;Username=postgres;Password=postgres");

    /// <summary>
    /// Maps an aggregate root's version onto the row's <c>xmin</c> system column as its concurrency token:
    /// the table gains no column, and PostgreSQL changes the value on every write of the row.
    /// </summary>
    public static PropertyBuilder<uint> MapRowVersion(PropertyBuilder<uint> version)
        => version
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

    /// <summary>Stores a JSON document held as a string in a <c>jsonb</c> column.</summary>
    public static PropertyBuilder<string> MapJson(PropertyBuilder<string> json)
        => json.HasColumnType("jsonb");

    /// <summary>
    /// Narrows the outbox index to the pending rows (<c>Status</c> 0, <see cref="OutboxMessageStatus.Pending"/>),
    /// a partial index: it stays small however much processed history the table holds.
    /// </summary>
    public static IndexBuilder<OutboxMessage> FilterToPendingMessages(IndexBuilder<OutboxMessage> index)
        => index.HasFilter("\"Status\" = 0");

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> pending messages that are due at <paramref name="now"/>,
    /// oldest first, and locks them for the caller's transaction. <c>FOR UPDATE SKIP LOCKED</c>: a second
    /// replica claims different rows instead of waiting for these. The query is run here, so no LINQ can be
    /// put on top of it: EF would wrap the locking SQL in a subquery, and the rows must stay tracked so the
    /// lease stamp is a plain save. <c>"Id"</c> (a time-ordered version-7 GUID) is the final key, so two
    /// rows written in the same microsecond are claimed in one order on every poll and every replica.
    /// </summary>
    public static Task<List<OutboxMessage>> ClaimDueMessagesAsync(
        DbSet<OutboxMessage> messages, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        => messages
            .FromSql($"""
                SELECT * FROM "OutboxMessages"
                WHERE "Status" = 0 AND "NextAttemptAtUtc" <= {now}
                ORDER BY "OccurredAtUtc", "Id"
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The name of the unique constraint a failed save violated (SQLSTATE 23505, which names the
    /// constraint). False for every other failure.
    /// </summary>
    public static bool TryGetViolatedUniqueConstraint(DbUpdateException exception, [NotNullWhen(true)] out string? constraintName)
    {
        if (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: { } name,
            })
        {
            constraintName = name;
            return true;
        }

        constraintName = null;
        return false;
    }

#if (UseMultitenancy)
    /// <summary>
    /// Reads the settings an index holds per column that only this provider has (the collation, the
    /// operator class and the NULLS order) and returns the write that gives them to the index that
    /// replaces it with one more column in front: each moves one place along, and the leading column
    /// takes the default. The values are stored as real arrays: a collection expression passed as an
    /// <c>IReadOnlyList&lt;T&gt;</c> becomes a list type the compiler makes up, which the C# migration
    /// generator cannot write (<c>migrations add</c> throws) and the provider's DDL skips without a word
    /// (the index loses its COLLATE, operator class and NULLS order).
    /// </summary>
    public static Action<Microsoft.EntityFrameworkCore.Metadata.IMutableIndex> CarryColumnSettingsBehindALeadingColumn(
        Microsoft.EntityFrameworkCore.Metadata.IMutableIndex index)
    {
        IReadOnlyList<string>? collation = index.GetCollation();
        IReadOnlyList<string>? operators = index.GetOperators();
        IReadOnlyList<Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NullSortOrder>? nullSortOrder = index.GetNullSortOrder();

        return replacement =>
        {
            if (collation is not null)
            {
                string[] shifted = [null!, .. collation];
                replacement.SetCollation(shifted);
            }

            if (operators is not null)
            {
                string[] shifted = [null!, .. operators];
                replacement.SetOperators(shifted);
            }

            if (nullSortOrder is not null)
            {
                Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NullSortOrder[] shifted =
                    [Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NullSortOrder.Unspecified, .. nullSortOrder];
                replacement.SetNullSortOrder(shifted);
            }
        };
    }
#endif
}
