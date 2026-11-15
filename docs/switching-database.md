# Switching the database engine

The solution runs on PostgreSQL, and no option changes that
([ADR-0033](adr/0033-postgresql-system-of-record.md)). This page lists the places a move to another
relational engine changes (the code and the tests that name the engine, the packages, the configuration
and tooling files outside C#, and the behaviour to prove again), each with the SQL Server equivalent
where the vendor's documentation gives one. The docs, rules and skills that mention PostgreSQL in prose
are not listed one by one: `grep -rIil -e postgres -e npgsql docs .claude scripts README.md AGENTS.md .gitleaks.toml`
finds them. The page was written from the code and from that documentation, and the solution has not
been run on another engine.

## Before you start

- **SQL Server.** There is no arm64 container image: on 2026-11-08 the `2025-latest` manifest listed
  `amd64` only. Microsoft: "SQL Server container images are supported only on Linux hosts running on
  Intel and AMD x86-64 CPUs. Emulation or translation environments (for example, Rosetta 2, Prism, or
  QEMU) aren't tested or supported." Azure SQL Edge "is retired as of September 30, 2025". On an arm64
  machine the integration tests and `aspire run` would depend on emulation the vendor does not support.
  The [editions of SQL Server 2025](https://learn.microsoft.com/en-us/sql/sql-server/editions-and-components-of-sql-server-2025?view=sql-server-ver17):
  Enterprise Developer and Standard Developer are free and "licensed for use as a development and test
  system, not as a production server"; Express is free, with limits (the lesser of one socket or four
  cores, a 1,410 MB buffer pool, 50 GB per database). The container image is
  `mcr.microsoft.com/mssql/server`, and it starts the Developer edition unless `MSSQL_PID` names
  another. [aspire.dev](https://aspire.dev/integrations/databases/sql-server/sql-server-host/) gives
  `2022-latest` as the default image tag of `AddSqlServer`, and the
  [`MsSqlBuilder.MsSqlImage`](https://github.com/testcontainers/testcontainers-dotnet/blob/develop/src/Testcontainers.MsSql/MsSqlBuilder.cs)
  constant of Testcontainers named `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04` on
  2026-11-08 (the constant was read, not how the builder uses it), so pin the tag in both places.
- **MySQL and MariaDB.** Blocked until an EF Core 10 provider exists under a license the policy
  accepts: on 2026-11-08 `Pomelo.EntityFrameworkCore.MySql` ended at 9.0.0, pinned to EF Core 9, and
  Oracle's `MySql.EntityFrameworkCore` is on the license ceiling list (the ADR's table has the sources).
- **SQLite, or a document store as the write store.** Not a port of this design: the ADR's table says
  what each lacks.
- **A document store beside PostgreSQL.** A read model fed by handlers of the outbox's events, not a
  switch (the ADR's fifth decision;
  [architecture.md](architecture.md#domain-events-and-integration-events)).

## Packages

Each package's version is in `Directory.Packages.props`, and the project files named after it reference
it. The SQL Server packages are MIT-licensed, from nuget.org.

- `Npgsql.EntityFrameworkCore.PostgreSQL` becomes `Microsoft.EntityFrameworkCore.SqlServer`, at the
  version of `Microsoft.EntityFrameworkCore` already pinned:
  `src/Infrastructure/Trellis.Persistence/Trellis.Persistence.csproj`.
- `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` becomes
  `Aspire.Microsoft.EntityFrameworkCore.SqlServer`:
  `src/Presentation/Api/Trellis.Api/Trellis.Api.csproj` and
  `tools/Trellis.MigrationService/Trellis.MigrationService.csproj`.
- `Aspire.Hosting.PostgreSQL` becomes `Aspire.Hosting.SqlServer`:
  `tools/Trellis.AppHost/Trellis.AppHost.csproj`.
- `Testcontainers.PostgreSql` becomes `Testcontainers.MsSql`:
  `tests/Trellis.IntegrationTests/Trellis.IntegrationTests.csproj` and
  `tests/Trellis.ApiTests/Trellis.ApiTests.csproj`.

A new package is a decision (`AGENTS.md`, *Ask first*, and the license ceilings in
`Directory.Packages.props`), and the server's container image needs a row with its license in
`ContainerImageTests.ImageAllowlist`.

## Persistence

Everything in Persistence that only PostgreSQL understands is in one file,
`src/Infrastructure/Trellis.Persistence/PostgresDialect.cs`; rewrite it member by member. Each row names
a member, the code that calls it, and what each engine does there. The paths in the table are under
`src/Infrastructure/Trellis.Persistence/`.

| Member of `PostgresDialect` | PostgreSQL | SQL Server | Not verified |
|---|---|---|---|
| `UseServer`, called by `DependencyInjection.AddPersistence` | `UseNpgsql` with `EnableRetryOnFailure` | `UseSqlServer` with `EnableRetryOnFailure`, and the same `CreateExecutionStrategy` pattern; `UseAzureSql` sets resiliency itself | |
| `UseDesignTime`, called by `AppDbContextFactory` | `UseNpgsql` without the retrying strategy, and a fallback connection string in Npgsql's syntax | the provider call without the retrying strategy, and a connection string in SQL Server's syntax | the connection string's syntax: no source of this page gives it |
| `MapRowVersion`, called by every aggregate root's configuration (`Configurations/InvoiceConfiguration.cs` in the sample) | `xmin`, type `xid`, read through a checked conversion into the `long` property, generated on add and update, a concurrency token; no column in the table | `rowversion`. *Handling concurrency conflicts* maps a `byte[]` with `[Timestamp]` or `IsRowVersion()`; *Value conversions* (section "Use ulong for timestamp/rowversion") calls the column "8-byte binary" and maps a `ulong` with `.IsRowVersion().HasConversion<byte[]>()`. It changes on every UPDATE of the row, so a change to a child alone still bumps it (`AggregateConcurrencyInterceptor`). It is a column of the table, so the migration changes | not run here; the documentation's example is a `ulong`, and a `long` mapped that way has not been run |
| `MapJson`, called by `Outbox/OutboxMessageConfiguration.cs` for the payload | `jsonb` | `json` when the provider is configured with `UseAzureSql` or `UseCompatibilityLevel(170)`; `nvarchar(max)` otherwise | |
| `FilterToPendingMessages`, called by `Outbox/OutboxMessageConfiguration.cs` for the index over the pending rows | `HasFilter("\"Status\" = 0")` | `HasFilter("[Status] = 0")` | |
| `ClaimDueMessagesAsync`, called by `Outbox/OutboxProcessor.cs` | the statement below | `SELECT TOP (@n) * FROM [OutboxMessages] WITH (UPDLOCK, READPAST, ROWLOCK) WHERE [Status] = 0 AND [NextAttemptAtUtc] <= @now ORDER BY [OccurredAtUtc], [Id]`. `READPAST` works only under READ COMMITTED or REPEATABLE READ, and not under READ COMMITTED with `READ_COMMITTED_SNAPSHOT` on unless `READCOMMITTEDLOCK` is added | how SQL Server orders a version-7 `uniqueidentifier`, the last key of the `ORDER BY` |
| `TryGetViolatedUniqueConstraint`, called by `UniqueConstraintErrors.TryMap` | `PostgresException`, SQLSTATE 23505, `ConstraintName` | `SqlException.Number` 2601 (a unique index) or 2627 (a constraint); the documented messages carry the name in their text, so parse it from there | whether `SqlException` gives the name any other way |

The claim as `ClaimDueMessagesAsync` runs it, with `now` and the batch size as its two parameters:

```sql
SELECT * FROM "OutboxMessages"
WHERE "Status" = 0 AND "NextAttemptAtUtc" <= {now}
ORDER BY "OccurredAtUtc", "Id"
LIMIT {batchSize}
FOR UPDATE SKIP LOCKED
```

Its meaning must survive the port: skip the rows another replica holds, lock the ones returned until
the transaction ends, and return them in one order on every replica. `OutboxRetryTests` is the proof
([deployment.md](deployment.md#outbox-operations) has the runbook).

One place is outside the dialect because it names no provider: `AppDbContext.ConfigureConventions` maps
every `DateTimeOffset` through `Conversions/UtcDateTimeOffsetConverter.cs`, because Npgsql refuses a
non-zero offset for `timestamptz`. Keep it on another engine: it keeps one convention. Which type the
SQL Server provider maps a `DateTimeOffset` to, whether that type keeps the offset, and its precision
are not verified.

Sources: the EF Core pages *Handling concurrency conflicts*
(<https://learn.microsoft.com/en-us/ef/core/saving/concurrency>, which shows the `byte[]` form only) and
*Value conversions* (<https://learn.microsoft.com/en-us/ef/core/modeling/value-conversions>, section
"Use ulong for timestamp/rowversion"), read on 2026-11-16. Every other SQL Server fact on this page is
from the sources ADR-0033 lists, read on 2026-11-08.

## How the list is held

`DatabaseEngineTests` (`tests/Trellis.ArchitectureTests/DatabaseEngineTests.cs`) fails when a C# file
outside its allowlist names the provider, a store type, a collation or SQL, and when a type of the
Persistence or the Api assembly outside the dialect calls into the provider's assemblies.

The allowlist is the list of C# files that name the engine, and the same class fails when this page
stops naming one of them. The sections after this one list what the allowlist cannot hold: the files
outside C#, the connection strings, and the behaviour to prove again. The files on the list:

- `src/Infrastructure/Trellis.Persistence/PostgresDialect.cs`: the dialect (*Persistence* above).
- `src/Presentation/Api/Trellis.Api/Program.cs`: the API's Aspire client settings for the provider
  (*Hosts and the AppHost*).
- `tools/Trellis.MigrationService/Program.cs`: the same settings in the migration worker.
- `tools/Trellis.AppHost/Program.cs`: the database server's resource, its image tag, its volume and its
  admin tool.
- `src/Shared/Trellis.ServiceDefaults/ServiceNames.cs`: the server's resource name, which the AppHost
  and two tests read.
- `tests/TestSupport/Database/PostgresTestDatabase.cs`: the tests' database server and provider
  (*Tests*).
- `tests/Trellis.IntegrationTests/DatabaseFixture.cs`: Respawn's adapter and the schema it empties.
- `tests/Trellis.IntegrationTests/OutboxRetryTests.Runbook.cs`: runs the runbook's re-drive statement
  as `docs/deployment.md` prints it.
- `tests/Trellis.ApiTests/AppHostTestingExtensions.cs`: swaps the AppHost's database server for a
  throwaway one, found by its resource name.
- `tests/Trellis.ApiTests/ThrowawayDatabaseTests.cs`: pins that swap against an AppHost model with the
  same resource.
- `tests/Trellis.ArchitectureTests/ContainerImageTests.cs`: the image allowlist names the server's image
  and the AppHost call that pulls it.

What the test does not see:

- a connection string, wherever it is written;
- a provider extension method called in `tools/` or `tests/` in a file that does not name the provider
  (the compiled check reads the Persistence and Api assemblies only);
- a store type or SQL handed to a member the pattern does not list;
- the range of the `If-Match` parser (*The aggregate's version*);
- behaviour (*Behaviour to prove again*).

A solution whose own name contains `Postgres` or `Npgsql` is a hit on every namespace line, because the
template engine writes the name into every namespace: except the name in the pattern,
`EngineSpecific()` in `DatabaseEngineTests.cs`.

<!--#if (UseMultitenancy) -->
### With multi-tenancy

- `PostgresDialect.CarryColumnSettingsBehindALeadingColumn` carries an index's collation, operator
  class and NULLS order, which are the provider's own settings per column, onto the tenant-led index
  that `AppDbContext.LeadWithTenant` puts in its place. It changes with the provider.
- `tests/Trellis.IntegrationTests/TenancyTests.cs` is on the list: it asserts those settings in the
  model and the exact `CREATE UNIQUE INDEX` text the migration writes.
- A row written with no tenant relies on NULLs being distinct in a unique index
  (`.claude/rules/persistence.md`, *Multi-tenancy*). What SQL Server does there is not verified.
- The migration is regenerated with the option anyway
  ([development.md](development.md#multi-tenancy-optional)).
<!--#endif -->

## The aggregate's version

`Version` is a `long` in the Domain and in Application (`Invoice.Version`, `InvoiceResponse`, the four
commands' `ExpectedVersion` and `InvoicePreconditions.ToWrite`) and at the contract (`InvoiceDto.Version`
and the ETag). A `uint` is left in two places:

- inside `PostgresDialect.MapRowVersion`: the checked conversion between the `long` and the `xid`
  column;
- in `HttpRequest.TryGetExpectedVersion`
  (`src/Presentation/Api/Trellis.Api.Common/Endpoints/ConditionalWriteExtensions.cs`), with the four
  endpoints that declare its `out uint?`: it refuses a tag the store could not have issued, with a 412
  before the lookup.

An engine with a wider row version changes both, and the `"4294967296"` row of `InvoiceContractTests`
(one past the widest version PostgreSQL issues, answered with a 412) with them. SQL Server's `rowversion` needs a
conversion to `byte[]`, and a `long` mapped that way has not been run (*Persistence*, the
`MapRowVersion` row).

## Migrations

Delete `Migrations/` and generate the initial migration with the new provider: the `add-migration`
skill, and the two commands of [upgrading.md](upgrading.md), step 5. A database that already holds
data needs its own data move, which this page does not cover. `MigrationDriftTests` then guards the new
set. The EF Core documentation's advice on keeping "multiple sets of migrations" applies only to a
project that keeps both engines.

## Hosts and the AppHost

| Where | PostgreSQL | SQL Server | Not verified |
|---|---|---|---|
| `src/Presentation/Api/Trellis.Api/Program.cs`, `tools/Trellis.MigrationService/Program.cs` | `EnrichNpgsqlDbContext` with `DisableRetry` (and, in the API, `DisableHealthChecks`) | `EnrichSqlServerDbContext` of `Aspire.Microsoft.EntityFrameworkCore.SqlServer` | its settings: check that it can turn its own retry and health check off, as the two calls do today |
| `tools/Trellis.AppHost/Program.cs` | `AddPostgres(…).WithImageTag("18").WithDataVolume().WithPersistentLifetime().WithPgAdmin()`, then `AddDatabase` | `AddSqlServer`. aspire.dev gives `2022-latest` as its default image tag, so pin the tag; it recommends a persistent lifetime because the server is slow to start, and warns that the password lives in the data volume | a counterpart to `WithPgAdmin()` |
| `src/Shared/Trellis.ServiceDefaults/ServiceNames.cs` | `Postgres = "postgres"` | rename the constant and its three readers (the AppHost, `AppHostTestingExtensions`, `ThrowawayDatabaseTests`) | |
| `tests/Trellis.ArchitectureTests/ContainerImageTests.cs` | the `postgres` and `pgadmin4` rows | the new image with its license: a decision under ADR-0011 | |

## Configuration outside C#

These files name PostgreSQL or carry a connection string in Npgsql's syntax; no equivalent is claimed
for them.

- The connection strings in `src/Presentation/Api/Trellis.Api/appsettings.Development.json`, in the two
  `Properties/launchSettings.json` files (the API's and the migration worker's), in
  `.devcontainer/devcontainer.json` and in the comment of `scripts/ef.sh`.
- The `postgres` service of `docker-compose.yml`: its image, its volume, its port and its `pg_isready`
  health check.
- The steps of `.github/workflows/ci.yml` that start PostgreSQL, and its connection strings.
- `.github/dependabot.yml`, which names the provider's packages twice: the `"Npgsql.*"` pattern of the
  `microsoft` group and the `Npgsql.*` semver-major ignore. The new provider's family needs both
  entries (`.claude/rules/dependencies.md`).
- The connection string in `ValidArguments()` of
  `tests/Trellis.ArchitectureTests/OptionsConventionTests.cs`, a C# file that neither fact of the
  fitness test can see, because a connection string names no provider type.

The other pages under `docs/`, the project's `README.md` and `AGENTS.md`, `scripts/doctor.sh`, the
comment in `.gitleaks.toml`, and the rules, skills and reviewer checklists under `.claude/` describe
PostgreSQL in words; the search in the opening lines of this page lists them.

## Tests

`tests/TestSupport/Database/PostgresTestDatabase.cs` is the one test file that starts the server and
configures the provider: it starts `postgres:18` through `Testcontainers.PostgreSql`, calls `UseNpgsql`
and reads a unique violation from `PostgresException`. Write the new engine's `ITestDatabase` beside it
(`tests/TestSupport/Database/ITestDatabase.cs` lists the members) and change the one line that chooses
the engine, `ITestDatabase.Create`. `Testcontainers.MsSql` is the SQL Server module.

The other test files on the list, each with what changes there:

- `tests/Trellis.IntegrationTests/DatabaseFixture.cs`: Respawn resets the data with
  `DbAdapter.Postgres` over the `public` schema. Respawn has a SQL Server adapter, not verified for the
  version `Directory.Packages.props` pins.
- `tests/Trellis.IntegrationTests/OutboxRetryTests.Runbook.cs`: the re-drive statement, which stays the
  runbook's text in [deployment.md](deployment.md#outbox-operations); the two change together.
- `tests/Trellis.ApiTests/AppHostTestingExtensions.cs` and `tests/Trellis.ApiTests/ThrowawayDatabaseTests.cs`:
  the Aspire test's throwaway database server, found by its resource name (`ServiceNames.Postgres`);
  `ThrowawayDatabaseTests` also declares the server with `AddPostgres`.
- `tests/Trellis.ArchitectureTests/ContainerImageTests.cs`: the image allowlist (*Hosts and the AppHost*).

A test of your own asks `Database.Server`, the fixture's `ITestDatabase`, for options over the server, a
model that never connects or the name of a violated unique constraint, instead of naming the provider
or starting a container of its own.

## Behaviour to prove again

Each item names its test and claims nothing about another engine.

- The claim under two processors and an expired lease (`OutboxRetryTests`).
- The outbox rows of one save sit one microsecond apart: the store must keep at least microseconds
  (`OutboxRetryTests`).
- The order of ids on the last key of a page: `SpecificationPagingTests` expects PostgreSQL's `uuid`
  order, the ordinal order of the canonical string.
- `"0"` and `"1"` are used as versions no invoice has (`InvoiceContractTests`,
  `ProblemDetailsContractTests`): check that neither can be a row's first version, or read the version
  and send another.
- A string holding U+0000 is a 400 at the API because PostgreSQL's `text` stores none. What another
  engine does is not known here, and the 400 can stay.
- The runbook's SQL in [deployment.md](deployment.md#outbox-operations) and the test that runs it.

## What is not known

- The syntax of the design-time connection string for SQL Server.
- Whether a `long` mapped over `rowversion` works: the documentation's example is a `ulong`.
- How SQL Server orders a version-7 `uniqueidentifier`, the last key of the claim's `ORDER BY` and of
  every page.
- Whether `SqlException` gives a violated constraint's name any other way than its message.
- Which type the SQL Server provider maps a `DateTimeOffset` to, whether that type keeps the offset, and
  its precision.
- The settings of `EnrichSqlServerDbContext`: whether it can turn its own retry and health check off.
- How `MsSqlBuilder` uses its image constant.
- Respawn's SQL Server adapter at the version `Directory.Packages.props` pins.
- Whether a SQL Server unique index treats NULLs as distinct (a tenant-less row relies on it with
  multi-tenancy).
- A counterpart to the pgAdmin tile in the AppHost.
- Identity columns and the length of identifiers, which the sources of this page do not cover.

## Order of work

1. The packages and the server's image, with its row in `ContainerImageTests`.
2. The test database: the new engine's `ITestDatabase` and Respawn's adapter, so that the tests run on
   the new engine.
3. The provider (`UseServer`, `UseDesignTime`) and the migration, until `MigrationDriftTests` passes.
4. The token (`MapRowVersion`), until `ConcurrencyTests` and `AggregateConcurrencyTests` pass.
5. The outbox (`MapJson`, `FilterToPendingMessages`, `ClaimDueMessagesAsync`), until `OutboxRetryTests`
   passes.
6. The unique violation (`TryGetViolatedUniqueConstraint`), until `UniqueConstraintTests`,
   `RegistrationRaceTests` and `IdempotencyStoreTests` pass.
7. The hosts and the AppHost.
8. `DatabaseEngineTests`: its pattern names this provider and this engine, so give it the new names, and
   keep its allowlist and this page in step.
9. The files outside C#, then the docs.
