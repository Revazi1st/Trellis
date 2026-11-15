# ADR-0033: PostgreSQL is the system of record, and the one engine the template ships

- **Status**: accepted
- **Date**: 2026-11-16

## Context

The question is whether the template should let a project choose its database engine, or ship one.
The solution depends on PostgreSQL in more places than its provider registration, and one of them,
the concurrency token, reaches the API as the ETag.

These are the ties. Each names the file that uses it (a path is under
`src/Infrastructure/Trellis.Persistence/` unless it starts with `src/`, `tools/` or `tests/`); the
*Decision* below says where the engine-specific code itself lives.

- The `xmin` system column is every aggregate root's concurrency token (mapped in the root's
  configuration, `Configurations/InvoiceConfiguration.cs` in the sample), and the API sends it as the
  ETag.
- The outbox payload is stored as `jsonb`, and a partial index covers the pending rows
  (`Outbox/OutboxMessageConfiguration.cs`).
- The outbox claim is `SELECT … FOR UPDATE SKIP LOCKED` (`Outbox/OutboxProcessor.cs`), which is what
  lets two replicas claim different rows (ADR-0023).
- A unique violation is read from `PostgresException`: SQLSTATE 23505 and the constraint's name
  (`UniqueConstraintErrors.cs`, ADR-0018).
- The shipped migration was written by the Npgsql provider (`Migrations/`).
- PostgreSQL orders `uuid` by its bytes, so a version-7 id is the last key of the claim's `ORDER BY`
  and of every page (`SpecificationPagingTests` asserts the order).
- Smaller ones: every instant is written as UTC for `timestamptz`
  (`Conversions/UtcDateTimeOffsetConverter.cs`); the outbox rows of one save sit one microsecond apart
  (`Interceptors/OutboxWriteInterceptor.cs`); a string holding U+0000 is refused at the API because
  `text` stores none (`.claude/rules/api.md`); the provider's retrying strategy
  (`DependencyInjection.cs`); the Aspire client settings in the two hosts; the AppHost's resource;
  `docker-compose.yml`; and the tests' container.

What each other engine would need, or lacks. Every row was read from the vendor's documentation, the
package registry or the project's repository on 2026-11-08; the sources are listed after the table.

| Engine | What stands in the way | Source |
|---|---|---|
| SQL Server 2025 | The EF Core provider, the Aspire hosting and client packages and the Testcontainers module are MIT, so nothing in the license policy (ADR-0011) refuses them. The design does not carry over as it is: `rowversion` instead of `xmin`; a claim written with `TOP … WITH (UPDLOCK, READPAST, ROWLOCK)`, where `READPAST` cannot be used under READ COMMITTED while `READ_COMMITTED_SNAPSHOT` is on unless `READCOMMITTEDLOCK` is added; EF Core 10 defaults to the `json` type only when the provider is configured with `UseAzureSql` or a compatibility level of 170 or higher, and to `nvarchar(max)` otherwise; a unique violation is error 2601 or 2627, whose documented messages carry the index's or the constraint's name in their text; and a second set of migrations. | EF Core docs (*What's new in EF Core 10*, *Handling concurrency conflicts*, the SQL Server provider); *Table hints* and *Database engine events and errors 2000 to 2999* on learn.microsoft.com |
| SQL Server on an arm64 machine | There is no arm64 container image: the `2025-latest` manifest listed `amd64` only, and the tag list held no arm64 tag. Microsoft: "SQL Server container images are supported only on Linux hosts running on Intel and AMD x86-64 CPUs. Emulation or translation environments (for example, Rosetta 2, Prism, or QEMU) aren't tested or supported." Azure SQL Edge "is retired as of September 30, 2025". On such a machine the integration tests and `aspire run` would depend on emulation the vendor does not support. | *Quickstart: run SQL Server Linux container images with Docker*; the registry's manifest and tag list for `mssql/server`; *Azure SQL Edge overview* |
| MySQL, MariaDB | No EF Core 10 provider under a license the policy accepts. `Pomelo.EntityFrameworkCore.MySql` (MIT) ends at 9.0.0 on nuget.org and depends on EF Core's relational package `[9.0.0, 9.0.999]`; its issue 2007, "EF Core 10 support", was open. Oracle's `MySql.EntityFrameworkCore` supports EF Core 10 and is GPL-2.0 with the Universal FOSS Exception, which is on the license ceiling list (`Directory.Packages.props`). | nuget.org (the package's version list and its 9.0.0 page); the Pomelo repository's issue 2007; ADR-0011 |
| SQLite | "Database-generated concurrency tokens" are not supported. Comparison and ordering of `DateTimeOffset` "aren't translated to SQL and have to be evaluated on the client", and the claim's `ORDER BY` and `<=`, the retention sweeps and the overdue filter depend on them. The database takes one writer at a time (`SQLITE_BUSY`), and there is no `SELECT … FOR UPDATE SKIP LOCKED`. | *SQLite EF Core provider limitations*; sqlite.org, *Result and error codes* |
| MongoDB (EF Core provider) | "Because MongoDB is a document database, migrations are not supported by the EF Core Provider". Transactions "require a MongoDB Server deployment that supports multi-document transactions, such as a replica set or sharded cluster". There is no `FromSql`, so a claim is one `findOneAndUpdate` per message through the driver. A BSON date keeps milliseconds, which collapses the outbox's microsecond spacing. The Community Server's license is the Server Side Public License v1. | mongodb.com docs for the EF Core provider (*Limitations*, *Transactions*); *BSON types*; the server's `LICENSE-Community.txt` |
| Oracle Database | The provider's license is Oracle's free-use terms (the package's `LICENSE.txt`), not an OSI-approved open-source license. | nuget.org, `Oracle.EntityFrameworkCore` 10.23.26400 |
| CockroachDB | It says it "works with most PostgreSQL drivers and ORMs", and its server license from 24.3.0 on is the CockroachDB Software License: paid, or free under conditions that include sending telemetry. | cockroachlabs.com docs, *Licensing FAQs* and *PostgreSQL compatibility* |

Sources, read on 2026-11-08:

- SQL Server:
  - <https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker?view=sql-server-ver17>
  - <https://mcr.microsoft.com/v2/mssql/server/manifests/2025-latest>
  - <https://mcr.microsoft.com/v2/mssql/server/tags/list>
  - <https://learn.microsoft.com/en-us/azure/azure-sql-edge/overview>
  - <https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2025?view=sql-server-ver17>
  - <https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew>
  - <https://learn.microsoft.com/en-us/ef/core/providers/sql-server/>
  - <https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17>
  - <https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/database-engine-events-and-errors-2000-to-2999?view=sql-server-ver17>
  - <https://learn.microsoft.com/en-us/ef/core/saving/concurrency>
  - <https://aspire.dev/integrations/databases/sql-server/sql-server-host/>
  - <https://github.com/testcontainers/testcontainers-dotnet/blob/develop/src/Testcontainers.MsSql/MsSqlBuilder.cs>
  - <https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/10.0.14>
  - <https://www.nuget.org/packages/Aspire.Microsoft.EntityFrameworkCore.SqlServer/13.6.4>
- MySQL and MariaDB:
  - <https://api.nuget.org/v3-flatcontainer/pomelo.entityframeworkcore.mysql/index.json>
  - <https://www.nuget.org/packages/Pomelo.EntityFrameworkCore.MySql/9.0.0>
  - <https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql>
  - <https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql/issues/2007>
  - <https://www.nuget.org/packages/MySql.EntityFrameworkCore>
  - <https://www.nuget.org/packages/Aspire.Pomelo.EntityFrameworkCore.MySql/13.6.4>
  - <https://dev.mysql.com/doc/refman/8.4/en/innodb-locking-reads.html>
  - <https://mariadb.com/docs/server/reference/sql-statements/data-manipulation/selecting-data/select>
  - <https://dev.mysql.com/doc/mysql-errors/8.4/en/server-error-reference.html>
  - <https://aspire.dev/integrations/databases/mysql/mysql-host/>
  - <https://github.com/mysql/mysql-server/blob/trunk/LICENSE>
  - <https://startdebugging.net/2026/10/fix-nu1608-nu1107-version-conflict-pomelo-entityframeworkcore-mysql-ef-core-10/>
- SQLite:
  - <https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations>
  - <https://www.sqlite.org/rescode.html>
  - <https://learn.microsoft.com/en-us/ef/core/saving/concurrency>
  - <https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite/10.0.14>
  - <https://www.nuget.org/packages/CommunityToolkit.Aspire.Hosting.Sqlite>
  - <https://www.nuget.org/packages/CommunityToolkit.Aspire.Microsoft.EntityFrameworkCore.Sqlite>
- MongoDB:
  - <https://www.nuget.org/packages/MongoDB.EntityFrameworkCore/10.0.5>
  - <https://github.com/mongodb/mongo-efcore-provider>
  - <https://www.mongodb.com/docs/entity-framework/current/limitations/>
  - <https://www.mongodb.com/docs/entity-framework/current/whats-new/>
  - <https://www.mongodb.com/docs/entity-framework/current/fundamentals/optimistic-concurrency/>
  - <https://www.mongodb.com/docs/entity-framework/current/interact-data/transactions/>
  - <https://www.mongodb.com/docs/manual/reference/method/db.collection.findOneAndUpdate/>
  - <https://www.mongodb.com/docs/manual/core/write-operations-atomicity/>
  - <https://www.mongodb.com/docs/manual/reference/error-codes/>
  - <https://www.mongodb.com/docs/manual/reference/bson-types/>
  - <https://aspire.dev/integrations/databases/mongodb/mongodb-host/>
  - <https://aspire.dev/whats-new/aspire-13-6/>
  - <https://github.com/testcontainers/testcontainers-dotnet/blob/develop/src/Testcontainers.MongoDb/MongoDbBuilder.cs>
  - <https://github.com/mongodb/mongo/blob/master/LICENSE-Community.txt>
- Oracle Database:
  - <https://www.nuget.org/packages/Oracle.EntityFrameworkCore/10.23.26400>
  - <https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/SELECT.html>
  - <https://github.com/gvenzl/oci-oracle-free>
  - <https://hub.docker.com/r/gvenzl/oracle-free>
  - <https://www.nuget.org/packages/Aspire.Oracle.EntityFrameworkCore/13.6.4>
- CockroachDB:
  - <https://docs.cockroachlabs.com/docs/stable/licensing-faqs>
  - <https://docs.cockroachlabs.com/docs/stable/select-for-update>
  - <https://docs.cockroachlabs.com/docs/stable/postgresql-compatibility>
  - <https://www.nuget.org/packages/Testcontainers.CockroachDb/4.16.0>
  - <https://hub.docker.com/r/cockroachdb/cockroach>

## Decision

- **PostgreSQL is the one database engine the template ships, and the system of record.** The
  aggregates, their outbox rows, the idempotency keys and the Identity tables are in one database,
  behind one EF Core context (`AppDbContext`), and a command's writes commit in one transaction.
- **No option changes the engine, and no provider abstraction is compiled in.** Exactly one EF Core
  provider, Npgsql's, is in the solution.
- **What only PostgreSQL understands has named homes, and a test holds the list of them.** In
  Persistence the home is one file, `PostgresDialect.cs`, which the provider registration, the
  design-time factory, the configurations, the outbox processor and `UniqueConstraintErrors` call. The
  others are the host wiring (the Aspire client settings in the API and the migration worker, the
  AppHost's resource), the generated migrations, and in the tests the one file that starts the server
  and configures the provider. A fitness test, `DatabaseEngineTests`, holds the list of files that may
  name the provider, a store type or SQL, and each entry carries its reason.
- **Moving to another relational engine is a port the project does by a checklist**,
  [switching-database.md](../switching-database.md). The template does not maintain a second engine.
- **A document store, a search index or a cache holds a copy for reading**, written by handlers of the
  outbox's events (ADR-0018, ADR-0023): idempotent, at least once, eventually consistent. It is never
  where a command writes.
- **A second relational engine, if the template ever offers one, is a `choice` option** that compiles
  one provider into the generated solution, with its own migrations and its own dialect file. It is
  never a layer that carries every provider at run time.

### The dialect

`PostgresDialect` is an `internal static class` in the `Trellis.Persistence` namespace. It is not an
interface, has no instance and is not registered in the container: nothing implements or replaces its
members at run time, and a project that moves to another engine rewrites the file member by member.
Its members:

- `UseServer` registers the provider over a connection string, with the retrying execution strategy
  every host runs under (`DependencyInjection.AddPersistence`). The outbox claim and the identity
  transactions run inside `CreateExecutionStrategy()` because of it.
- `UseDesignTime` registers the provider without the retrying strategy, for the context `dotnet ef`
  builds (`AppDbContextFactory`), with the fallback connection string of a throwaway local server. The
  two are separate members because the host's context retries and the design-time context does not,
  and one member would change one of them; `DatabaseWiringTests` pins both.
- `MapRowVersion` maps an aggregate root's version onto the `xmin` system column (type `xid`,
  generated on add and update, a concurrency token) through a checked conversion (below).
- `MapJson` stores the outbox payload as `jsonb`.
- `FilterToPendingMessages` narrows the outbox index to the pending rows, `"Status" = 0`. It is a
  member about the outbox, not a pass-through for any filter, so that the SQL text stays in the dialect.
- `ClaimDueMessagesAsync` runs the claim, `FOR UPDATE SKIP LOCKED`, and returns the tracked rows. No
  LINQ can be put on top of the locking SQL, because the caller never holds the query. The execution
  strategy, the transaction and the lease stamp stay in `OutboxProcessor`.
- `TryGetViolatedUniqueConstraint` reads the name of the unique constraint a failed save violated from
  `PostgresException` with SQLSTATE 23505. `UniqueConstraintErrors` keeps the map from names to errors.

<!--#if (UseMultitenancy) -->
With multi-tenancy the file has one more member, `CarryColumnSettingsBehindALeadingColumn`: it reads an
index's collation, operator class and NULLS order, which are the provider's own settings per column,
and returns the write that gives them to the tenant-led index replacing it in `AppDbContext`, each moved
one place along behind the leading column.
<!--#endif -->

What stays outside the dialect, and why:

- `EnrichNpgsqlDbContext` in `src/Presentation/Api/Trellis.Api/Program.cs` and
  `tools/Trellis.MigrationService/Program.cs`. It is an extension of the Aspire client package, which
  only the two hosts reference: moving it would add that package to Persistence, and an `internal`
  class is invisible to the hosts anyway.
- The AppHost's resource (`tools/Trellis.AppHost/Program.cs`: the server, its image tag, its volume,
  its admin tool) and the resource's name in `src/Shared/Trellis.ServiceDefaults/ServiceNames.cs`:
  ServiceDefaults depends on no solution layer, and the AppHost and two tests read the name.
- `Migrations/`, generated by `dotnet ef` and never hand-edited. The fitness test leaves the folder out
  by its path.
- `UtcDateTimeOffsetConverter` and the one-microsecond spacing of the outbox rows: plain code with no
  provider call, whose reason is PostgreSQL's.
- The refusal of U+0000 in `Trellis.Api.Common`: it runs at JSON binding and decides a status, and
  moving it would change the 400.
- `Invoice.Version`: the Domain cannot reference Persistence, so its type is neutral instead (below).
- The connection strings and every file that is not C#, which the guide lists.

### The fitness test

`DatabaseEngineTests` (ArchitectureTests) has three facts, each with a positive control:

- `Only_the_listed_files_name_the_database_engine_or_write_its_SQL` scans every C# file under `src/`,
  `tools/` and `tests/`, the generated migrations aside. A hit is the provider's name; the engine's name
  in an identifier, the dialect's and the test database's own names aside; a call that hands the
  database a store type, a collation, SQL text or a database-generated row version; or the same written
  as a mapping attribute or as an ADO.NET command's text. Its allowlist is the list of C# files that
  name the engine, each with its reason. An entry exempts its whole file, so a test whose subject is the
  engine sits in a file as small as that subject, and an entry for a file only an option ships is held
  only where the file exists.
- `Only_the_dialect_and_the_migrations_call_into_the_provider` reads the compiled Persistence and Api
  assemblies, through NetArchTest's custom rule over Mono.Cecil, which NetArchTest already depends on
  (no package is added). A type outside `PostgresDialect` and the migrations whose signatures or method
  bodies use a type of the provider's assemblies fails, and the bodies the compiler moves into nested
  types of its own (a lambda, an async method, an iterator) count as the type's. It catches a provider
  extension method whose name does not say whose it is, such as an index's `HasMethod` or
  `EF.Functions.ILike`.
- `The_switching_guide_names_every_file_on_the_list` fails when a path on the allowlist is not written
  in full in [switching-database.md](../switching-database.md), so the checklist cannot fall behind the
  code.

None of them sees a connection string, a provider extension method called in `tools/` or `tests/` in a
file that does not name the provider (the compiled check reads two assemblies), a store type or SQL
handed to a member the pattern does not list, the range of versions the `If-Match` parser accepts, or
code that is neutral in text and in its references but relies on how the engine behaves. The guide lists
those by hand.

### The test database

The tests reach the database through `ITestDatabase` (`tests/TestSupport/Database/`, linked into
IntegrationTests and ApiTests). IntegrationTests' `DatabaseFixture` and ApiTests' `ApiFactory` each hold
one, and a test asks it for options over the server, for a model that never connects and for the name of
a violated unique constraint instead of naming the provider. `ITestDatabase.Create` is the one line that
chooses the engine, and `PostgresTestDatabase` is the one test file that starts the server and configures
the provider. Respawn's adapter and the schema it empties stay in `DatabaseFixture.cs`, because only
IntegrationTests references Respawn. The other test files that name the engine do so because the engine
is their subject, and each is on the allowlist with its reason. This is test support, not a production
abstraction, so it does not contradict the second decision.

### The aggregate's version

`Invoice.Version` is a `long`, and so is everything that carries it in the Domain and in Application:
`InvoiceResponse.Version`, the commands' `ExpectedVersion` (`long?`) and `InvoicePreconditions.ToWrite`.
`uint` is the provider's type for `xid`; with `long` the Domain and Application no longer carry the
engine's type. `MapRowVersion` reads the column through a checked conversion, so a value the database
could not have issued (above `4294967295`, or negative) throws `OverflowException` instead of wrapping to
another row's version.

`HttpRequest.TryGetExpectedVersion` keeps reading a `uint?` (ADR-0025): that is the range of versions
this store can issue, so a strong tag above it is still refused with a 412 before the lookup, and a
`uint?` widens into the command's `long?` without a cast. Widening the parser would change answers: such a
tag would reach the pipeline, and a missing invoice would answer 404 and an invalid body 400 where both
answer 412 today. It would also make the checked conversion reachable from a request header.

Nothing a client or a database sees changed with this record: the schema and the model snapshot (the
shipped migration is the same file), the SQL the token produces, the OpenAPI document and every answer of
the API.

## Consequences

- A project that must run on another relational engine ports it itself, by
  [switching-database.md](../switching-database.md). The places are named, and the fitness test keeps
  the list of C# files among them current.
- The solution keeps what PostgreSQL gives it: a concurrency token that costs no column, a claim that
  scales to several replicas, `jsonb`, a partial index, and constraint names in errors.
- A generated project has one provider's packages in its graph and one set of migrations.
- The checklist is written from the code and the vendors' documentation, and nobody has run the solution
  on another engine. The guide says so, and lists what is not known.
- A rule for whoever adds persistence code or a test: a store type, a collation, an index filter, raw SQL
  or a provider call goes into the dialect, and a test asks the test database instead of naming the
  provider or starting a container of its own. `.claude/rules/persistence.md` and `.claude/rules/tests.md`
  restate it. Code that cannot live there goes on the allowlist with its reason, and into the guide.
- A solution whose own name contains `Postgres` or `Npgsql` is a hit on every namespace line of the scan,
  because the template engine writes the name into every namespace. The scan's failure message names the
  fix: except the name in the pattern, `EngineSpecific()` in `DatabaseEngineTests.cs`.
- What a project generated from an earlier version meets when it takes this change: no database change
  and no migration; the sample's files that carry the version (the aggregate, its response, its
  commands, its preconditions and its configuration), taken as they are or merged; an aggregate of its own with a `uint Version` keeps working at run time, and fails the scan
  once the project takes `DatabaseEngineTests` until the property is a `long` and its configuration calls
  `PostgresDialect.MapRowVersion` (the model snapshot stays the same); any other store type, index
  filter or raw SQL in its Persistence, and any test that names the provider or starts its own container,
  fails the scan until it moves into the dialect, asks the test database, or goes on the allowlist with
  its reason. Its endpoints keep compiling, and API clients see nothing.
- Not decided here: the `choice` option itself, a second set of migrations, a read-model recipe, and SQL
  Server test legs.

## Alternatives / Revisit when

- *A database option now*: two shipped migrations on top of the multi-tenancy regeneration, a second
  claim to keep equal in meaning to the first, every persistence test run twice, and SQL Server legs that
  cannot run in a supported way on an arm64 machine.
- *Every provider compiled in, chosen by configuration*: every project carries every provider's packages
  and one set of migrations per provider, and the model itself differs per engine (the token).
- *A document store as the write store*: no migrations, and the commit boundary of aggregate, outbox row
  and idempotency key would need a replica set.
- Revisit when a supported arm64 container image of SQL Server exists; when Pomelo publishes a release
  for EF Core 10 (MySQL then needs the same list of changes); or when a project asks for SQL Server and is
  willing to carry its legs.
