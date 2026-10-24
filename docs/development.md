# Development

## Prerequisites

| Tool | Needed for |
|---|---|
| **Docker** (Docker Desktop, OrbStack, Colima, Linux Docker Engine; Podman with caveats, see [below](#docker-engines-other-than-docker-desktop)) | everything: PostgreSQL, Testcontainers, the SDK container |
| **.NET 10 SDK** on the host | `aspire run` and the Aspire E2E test (ADR-0008). Build, tests and the dev-container flow work without it, through the SDK container |
| **Aspire CLI 13.5 or later**: `dotnet tool install -g Aspire.Cli` | `aspire run` / `aspire publish`. A separate install from the SDK; the AppHost is on Aspire 13.6.1, and `aspire --version` shows what you have. Without it, `dotnet run --project tools/Trellis.AppHost` starts the same app |
| **PowerShell** (`pwsh`) | installing the Playwright browsers for the optional UI tests |

No .NET SDK on the host? Build the SDK image once (base SDK + docker CLI, needed by
Testcontainers/Aspire) and run every command through `./scripts/dotnet.sh`:

```bash
docker build -t trellis-sdk:10.0 scripts/sdk-image
```

## IDE: open in the dev container (no host SDK)

Without a .NET SDK on the host an IDE cannot resolve types locally.
`.devcontainer/devcontainer.json` fixes that by running the IDE backend *inside* the same SDK
image the scripts use. With a host SDK you can skip this section and open the solution directly.

**Rider**: open the solution → Rider detects `.devcontainer` and offers *"Reopen in Dev Container"*
(or use **File → Remote Development → Dev Containers**). VS Code: *Dev Containers: Reopen in Container*.

You then get full IntelliSense, build, debug, and test in the IDE, while nothing is installed on the
host. The container mounts the Docker socket, so Testcontainers integration tests and the Aspire
orchestrator work from inside it too, and it shares the `trellis-nuget` package volume with
`./scripts/dotnet.sh` (same `/workspace` path, so build output stays valid across both).

> Without the dev container the IDE will show unresolved-type errors for the whole BCL. That is
> expected on a host with no SDK — it is not a problem with the code.

## Everyday commands

```bash
./scripts/dotnet.sh build Trellis.slnx            # build
./scripts/dotnet.sh test Trellis.slnx             # all tests (Aspire E2E skips unless RUN_ASPIRE_E2E=1)
./scripts/dotnet.sh format Trellis.slnx           # apply formatting
./scripts/ef.sh migrations add <Name> --project src/Infrastructure/Trellis.Persistence
```

### Docker engines other than Docker Desktop

The SDK container starts the Testcontainers PostgreSQL as a *sibling* container through the
engine's socket, then reaches its published port via `host.docker.internal`. Docker Desktop
resolves that name by itself; everywhere else `scripts/dotnet.sh`, `scripts/ef.sh` and the dev
container map it to the host gateway (`--add-host=host.docker.internal:host-gateway`).
`scripts/dotnet.sh` picks the socket to mount like this:

| Setup | What to set |
|---|---|
| Docker Desktop, OrbStack, Linux Docker Engine | nothing: `/var/run/docker.sock` |
| Colima (macOS) | nothing, with or without `DOCKER_HOST`: on macOS the engine runs in a VM and resolves mount sources there, so the script always mounts the VM's `/var/run/docker.sock` |
| Rootless Docker (Linux) | `DOCKER_HOST=unix://$XDG_RUNTIME_DIR/docker.sock`, which the rootless setup tells you to set anyway; the script mounts that path |
| Podman 5+ (Linux) | `DOCKER_HOST=unix://$XDG_RUNTIME_DIR/podman/podman.sock` (after `systemctl --user enable --now podman.socket`); the docker CLI then drives Podman, or set `DOCKER_BIN=podman`. Rootless Podman also needs `TESTCONTAINERS_RYUK_DISABLED=true`, as Testcontainers recommends |
| Podman machine (macOS) | a rootful machine (`podman machine set --rootful`), its API socket in `DOCKER_HOST` (or `DOCKER_BIN=podman`), and `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/run/podman/podman.sock`, the socket's path inside the VM |
| A TCP endpoint (`DOCKER_HOST=tcp://…`) | nothing: the script passes `DOCKER_HOST` through and mounts no socket. The address must be reachable from inside a container, so not `127.0.0.1` |

`TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE` is the socket path *as the engine sees it*. When set it
overrides all of the above: the script mounts it and hands it to Testcontainers for its resource
reaper. Every other `TESTCONTAINERS_*` variable in your shell is forwarded into the container, so
the [Testcontainers recipes](https://dotnet.testcontainers.org/custom_configuration/) apply
unchanged. The dev container mounts `/var/run/docker.sock`; for rootless Docker or Podman, change
the `source=` of that mount in `.devcontainer/devcontainer.json` to your socket. This
repository's own runs exercise two of these paths: Docker Desktop, and Linux Docker Engine in the
`sdk-container` CI job (`scripts/dotnet.sh` on `ubuntu-latest`). The others follow the engines'
and Testcontainers' documentation.

## Secrets

`Authentication:SigningKey` gates API startup — missing or under 32 characters and the API
refuses to boot. `appsettings.Development.json` ships a dev-only key so F5 works out of the box;
to use your own, set a user secret (the API csproj has a `UserSecretsId`):

```bash
dotnet user-secrets set "Authentication:SigningKey" "<random 32+ chars>" \
    --project src/Presentation/Api/Trellis.Api
```

E-mail is logged, not sent, until you point the API at an SMTP relay the same way (port 587 with
STARTTLS is the default; leave out the last two lines for a relay without authentication):

```bash
api=src/Presentation/Api/Trellis.Api
dotnet user-secrets set "Email:Provider" "Smtp" --project $api
dotnet user-secrets set "Email:Smtp:Host" "smtp.example.com" --project $api
dotnet user-secrets set "Email:Smtp:FromAddress" "billing@example.com" --project $api
dotnet user-secrets set "Email:Smtp:UserName" "<relay user>" --project $api
dotnet user-secrets set "Email:Smtp:Password" "<relay password>" --project $api
```

To see the mail without a relay, run a local catcher such as Mailpit
(`docker run --rm -p 1025:1025 -p 8025:8025 axllent/mailpit`) and set `Email:Smtp:Host` to
`localhost` (`host.docker.internal` from the dev container), `Email:Smtp:Port` to `1025` and
`Email:Smtp:UseStartTls` to `false`, with no credentials; messages appear at <http://localhost:8025>.

Run these where the app runs (dev container or local SDK) — user secrets live under that
environment's home directory, so setting them through the ephemeral `./scripts/dotnet.sh`
container does not persist. Never commit a real key; production takes it from the
environment/secret store (see [deployment.md](deployment.md)).

## Running the app

Seeded admin for every option below: `admin@example.com` / `Admin123!`.

### From the IDE, in the dev container (no local SDK)

`aspire run` **cannot** drive the app from inside a container — the orchestrator would run the
projects in the container while starting PostgreSQL as a sibling on the host, and the two cannot
reach each other (ADR-0008). Run the dependencies with Compose and the projects from the IDE:

```bash
docker compose up -d          # PostgreSQL on host port 5433
```

Then in Rider/VS Code, using the **`http (dev container)`** launch profiles — they point at
`host.docker.internal:5433`, because inside the dev container `localhost` is the container itself:

1. Run **Trellis.MigrationService** (profile `migrate (dev container)`) once — applies migrations and seeds.
2. Run **Trellis.Api** (profile `http (dev container)`) → <http://localhost:5171>, Scalar at `/scalar/v1`.
3. Run **Trellis.UI.Web** (profile `http (dev container)`) → <http://localhost:5170>.

**Any run configuration works**, because `devcontainer.json` sets `ASPNETCORE_ENVIRONMENT` and the
connection string via `containerEnv` — every process in the container inherits them. That matters
because Rider generates *two* configurations per project and they look almost identical:

| Configuration | Type | Port |
|---|---|---|
| `Trellis.Api: http (dev container)` | .NET Launch Settings Profile | 5171 (5170 for the UI) |
| `Trellis.Api` | .NET Project — **ignores `launchSettings.json` entirely** | 8080 (the SDK image default) |

The bare one runs the built binary with no arguments and none of the profile's environment, so
before the `containerEnv` fix it died with `Connection string 'trellis-db' is not configured`.
Prefer the profile configurations: only they give the API and the UI *distinct* ports.

Ports reach the host browser because Rider forwards them (the client binds `127.0.0.1:<port>`) —
it forwards what `forwardPorts` declares, which is why 8080 is listed too. Never add `appPort`:
`docker -p` would bind the same host ports and collide with that forwarder.

> **Two gotchas worth knowing.** The connection string is also passed as a *command-line argument*
> in the profiles, because the env form `ConnectionStrings__trellis-db` contains a dash — not a
> valid POSIX shell identifier — so `sh`-wrapped launchers silently drop it. And
> `appsettings.Development.json` points at `host.docker.internal`, with the host `http`/`https`
> profiles overriding back to `localhost`.

> Port **5433**, not 5432: a native PostgreSQL install commonly owns 5432, and the clash fails
> confusingly — it connects, then rejects the password. Override with `POSTGRES_PORT` and match it
> in the launch profile. Redis (for `--use-redis`) is behind a profile: `docker compose --profile redis up -d`.

### With a local SDK

```bash
aspire run                                   # needs the Aspire CLI (Prerequisites)
dotnet run --project tools/Trellis.AppHost   # the same app, no CLI needed
```

PostgreSQL, the migration worker, the API, the Blazor web app, and the Aspire dashboard, all
wired together, plus pgAdmin (and RedisInsight with `--use-redis`) as dashboard tiles that open
without a login; open the dashboard URL either command prints. This is the best loop when a host
SDK is available. The Aspire CLI reads the AppHost path from the committed `aspire.config.json`
(`appHost.path`), the project settings file it would otherwise create on first use.

From Rider or Visual Studio, run the **Trellis.AppHost** project: its `Properties/launchSettings.json`
has an `https` profile (the dashboard at `https://localhost:17171`) and an `http` profile
(`http://localhost:15171`, no certificate involved); the ports are picked free when the solution is
created, so this document names yours. The IDE's Aspire run configuration needs one of
them; without a launch profile it refuses to start, while `dotnet run` and `aspire run` fall back to
a dynamic dashboard URL. The `https` profile wants a trusted development certificate: run
`dotnet dev-certs https --trust` once (macOS and Windows prompt for confirmation), or use `http`.
The AppHost's own profile is the only one Aspire reads: the API, web and migration projects run
with `ExcludeLaunchProfile = true` and get their endpoints and connection string from Aspire
(the comment in `tools/Trellis.AppHost/Program.cs` says why). The AppHost reads its `appsettings.json`
from the project folder; it only sets log levels, so the dashboard's request logs stay out of the console.

On the first start against an empty database the `migrations` resource logs one red EF Core line,
`Failed executing DbCommand … SELECT "MigrationId", "ProductVersion" FROM "__EFMigrationsHistory"`,
and PostgreSQL logs `relation "__EFMigrationsHistory" does not exist`. EF Core probes the history
table by querying it, then creates it and applies the migrations; the resource still exits with
code 0 and the API starts, because the API waits for exactly that. A real migration failure leaves
the API waiting, with the exception in the `migrations` console log.

### Containerized smoke

See the `run-app` skill — Postgres + migration worker + API (+ web) as sibling containers on one network.

## Multi-tenancy (optional)

Generate with `--use-multitenancy` to add the tenant seam: `ITenantContext`, resolved from the
**`tenant` claim only** — minted at login from the user's persisted membership
(`AspNetUsers.TenantId`); there is no tenant header, and a token without the claim is rejected.
Every aggregate root automatically gets a `TenantId` shadow column stamped by `TenantInterceptor`
and a named `"Tenant"` EF query filter (opt out with `IgnoreQueryFilters(["Tenant"])`). The seeded
admin and the demo data are in tenant `default` (`Seed__TenantId`); a self-registered user gets a
fresh tenant. See ADR-0019 and ADR-0022.

Because this adds a column, **regenerate the initial migration** in the generated project:

```bash
./scripts/ef.sh migrations remove --project src/Infrastructure/Trellis.Persistence
./scripts/ef.sh migrations add InitialCreate --project src/Infrastructure/Trellis.Persistence
```

## UI tests (optional)

Generate with `--include-ui-tests` to add `tests/Trellis.UiTests` (Playwright). The project always
builds, but the browser tests are **opt-in at run time** — they need real browsers and a running app
at a known address. `UI_TESTS_BASE_URL` names it (default `https://localhost:7170`, the web host's
`https` launch profile). `aspire run` gives the web host a port of its own choosing, so either copy the
`web` URL from the dashboard into `UI_TESTS_BASE_URL`, or run the projects from their launch profiles
(migration job, then the API's and the web host's `https` profiles) so the default fits:

```bash
dotnet build tests/Trellis.UiTests                                        # creates playwright.ps1
pwsh artifacts/bin/Trellis.UiTests/debug/playwright.ps1 install chromium  # once
RUN_UI_TESTS=1 UI_TESTS_BASE_URL=http://localhost:5170 dotnet test tests/Trellis.UiTests   # the http profile
```

Without `RUN_UI_TESTS=1` they skip, so the normal suite stays fast and browser-free
(the same opt-in pattern as the Aspire E2E test).

## Tests

This is the map of the test projects: what each one proves about which layer, what it needs to run, and
when a test of yours belongs in it. The README, `.claude/rules/tests.md` (the conventions inside each
project) and the `add-feature` skill point here rather than repeat it.

| Project | Proves | Needs | Write a test here when |
|---|---|---|---|
| `Domain.UnitTests` | the Domain and the SharedKernel in memory: every invariant and transition of an aggregate, the events it raises, value objects, specifications as predicates, the `Result` kernel | nothing | you add or change a rule of an aggregate or a value object |
| `Application.UnitTests` | each handler's orchestration over faked ports (NSubstitute, `FakeTimeProvider`), the validators, event handlers, and the decorator pipeline itself (order, span, outcome line, validation) | nothing | you write a handler, a validator or an event handler, or touch a decorator: one folder per use case under `Features/<Feature>/<UseCase>/`, the validators' bounds in `Features/<Feature>/<Feature>CommandValidatorTests.cs` |
| `ArchitectureTests` | the rules files hold: layer boundaries, conventions by reflection and source scan, the error-code vocabulary, options validated at boot, telemetry names and the telemetry reference | nothing | you introduce a convention: it gets a rule and a positive control, not just prose |
| `IntegrationTests` | Persistence against a real PostgreSQL: mappings and round trips, specifications translated to SQL, interceptors, concurrency, the outbox and the idempotency store, retention, the migration matching the model | Docker (Testcontainers starts PostgreSQL) | a query, a mapping, an interceptor or anything transactional changes |
| `ApiTests` | the HTTP contract through the real pipeline: authentication and each endpoint's permission, problem details, idempotency, caching and conditional requests, the served OpenAPI document equal to the committed one, the reference integration over a scripted socket | Docker for the in-process tests (contract and serialization tests run without it). The Aspire E2E also needs `RUN_ASPIRE_E2E=1` and a local SDK or CI, otherwise it skips; it drops the AppHost's postgres data volume, so it runs on a throwaway database and never touches your `aspire run` data | an endpoint answers something the fitness suites do not already pin: its own 404, a business 409, the field codes of its 400, a notable flow end to end |

`tests/TestSupport/` is not a project: it holds the test files several projects share
(`ResultAssertions.cs` with `ShouldBeSuccess()`, `Invoices/InvoiceFactory.cs`), each compiled into the
projects that need it by a linked `<Compile Include>` in their csproj.

<!--#if (IncludeUI || UseMultitenancy) -->
Suites a template option adds, present only in a project generated with it:
<!--#endif -->
<!--#if (IncludeUI) -->
- **`ApiTests/Ui`** (with the UI; optional): UI.Common's typed clients, token handling and form logic over a
  scripted `HttpMessageHandler` and a manual clock, no browser. Needs nothing more than the rest of
  `ApiTests`. Write one when a client, a form or a page's behaviour changes.
<!--#endif -->
<!--#if (IncludeUiTests && IncludeUI) -->
- **`UiTests`** (`--include-ui-tests`; optional, and opt-in at run time): the pages in a real browser
  against a running app. Needs `RUN_UI_TESTS=1`, installed browsers and a running app (see *UI tests*
  above), otherwise it skips. Write one for a flow only a browser shows; keep them few.
<!--#endif -->
<!--#if (UseMultitenancy) -->
- **`TenancyTests`** (IntegrationTests) and **`TenantIsolationTests`** (ApiTests), with multi-tenancy
  (optional): stamping, filtering and the aggregate-root convention against PostgreSQL, and cross-tenant
  reads and writes answering 404 over HTTP. Write one when an aggregate is deliberately shared or a
  query opts out of the `"Tenant"` filter.
<!--#endif -->

Where to invest: most of a feature's tests belong in `Domain.UnitTests` and `IntegrationTests`, because a
rule is cheapest to pin where it lives and a query or a transaction only proves itself against
PostgreSQL. Every endpoint's permission, problem shape, idempotency and OpenAPI entry is already enforced
by the fitness suites, so an `ApiTests` test is for what they cannot see, and the Aspire E2E and browser
tests stay few.

### Running part of the suite

The test projects run on Microsoft.Testing.Platform (xUnit v3). Narrow a run to one project, then to a
class or a method by pattern:

```bash
./scripts/dotnet.sh test tests/Trellis.Domain.UnitTests                                    # one project
./scripts/dotnet.sh test tests/Trellis.Application.UnitTests --filter-class '*CommandHandlerTests'
./scripts/dotnet.sh test tests/Trellis.ArchitectureTests --filter-method '*telemetry_reference*'
./scripts/dotnet.sh test tests/Trellis.ApiTests --filter 'FullyQualifiedName~Idempotency'  # the VSTest-style expression works too
```

Filter a project, not `Trellis.slnx`: every other project then runs zero tests and exits 8, so the run is
red although your test passed (add `--ignore-exit-code 8` if you must filter the solution). VSTest-only
switches such as `--nologo` are not understood either, and a run that reports zero tests is a failure to
look at, never a pass.

### Which helper for which goal

| Goal | Use | Worked example |
|---|---|---|
| A caller holding exactly some permissions | `ApiFactory.CreateClientWithPermissions(...)`: a fresh user each call, minted by the host's `ITokenProvider` | `EndpointMetadataTests` |
| The seeded admin, or a member who can only read | `ApiFactory.CreateAdminClientAsync`, `CreateMemberClientAsync` | `ProblemDetailsContractTests` |
| Many users behind one address (a proxy, the web host) | `ApiFactory.CreateClientFrom(address)` | `AuthRateLimitTests` |
| A commit that conflicts or throws, a cache that fails | `ApiFactory.WithTestServices(...)`, then `CreateAdminClientOnAsync(host, …)` | `CommitFailureTests` |
| A valid command or aggregate in an arrange step | `InvoiceFactory.CreateCommand(...)`, `InvoiceFactory.DraftWithLine()`, `InvoiceFactory.Issued(...)` (`using Trellis.TestSupport.Invoices;`) | `IssueInvoiceCommandHandlerTests`, `InvoiceCommandValidatorTests` |
| An invoice in a given state against PostgreSQL | `await Services.CreateInvoiceAsync()`, `IssuedInvoiceAsync()`, `PayInvoiceAsync(id)` (`InvoiceDriver`; on a fresh scope's `ServiceProvider` for a separate unit of work) | `OutboxTests` |
| An invoice over HTTP in an arrange step | `await client.CreateInvoiceAsync()`, `AddInvoiceLineAsync(id)`, `IssueInvoiceAsync(id, dueDate)` (`InvoiceHttpDriver`) | `ProblemDetailsContractTests` |
| A POST the drivers do not cover (a test about the create's own answer) | `PostWithIdempotencyKeyAsync` | `InvoiceContractTests` |
| Acting as another user against PostgreSQL | `IntegrationTestBase.CurrentUser` (`TestCurrentUser`): set its `UserName` or `UserId` | `AuditingInterceptorTests` |
| A `Result` that must have succeeded | `ShouldBeSuccess()` (`tests/TestSupport/ResultAssertions.cs`), which returns a `Result<T>`'s value and on failure names the error | `InvoiceTests` (the message: `ResultAssertionsTests`) |
| A problem response, header and body | `ShouldBeProblemJson()`, then read `ProblemResponse` | `ProblemDetailsContractTests` |
| A new status or problem `code` | a row in `ProblemDetailsContractTests` | — |
| Time that passes without sleeping | `IntegrationTestBase.Time`, `ApiFactory.Time` (`Reset()` it in a `finally`), `FakeTimeProvider` | `DataRetentionJanitorTests` |
| A limit only Kestrel enforces | `RequestLimitTests.KestrelApiFactory` | `EndpointMetadataTests.cs` |
| Observers instead of mocks: a clock, a hasher, captured logs | `AuthFlowTests.Factory` | `AuthFlowTests` |
| The shipped defaults, not the fixture's settings | a plain `WebApplicationFactory<Program>` | `AuthFlowTests.Register_WithTheShippedProductionConfiguration_IsClosed` |
| The outcome line and the span of a use case | `PipelineHarness` and its `Logs` | `UseCaseOutcomeTests` |
| Metrics and spans against PostgreSQL | `MetricRecorder`, `ActivityRecorder`, `IntegrationTestBase.MeterFactory` | `OutboxRetryTests` |
| A wire change | `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/Trellis.ApiTests`, then commit `docs/api/openapi.v1.json` | `OpenApiSnapshotTests` |
<!--#if (UseMultitenancy) -->

With multi-tenancy, `ApiFactory.CreateAdminClientAsync(tenantId, …)` signs in an administrator of another
tenant (created on first use); `TenantIsolationTests` is the worked example.
<!--#endif -->

## When the build fails

- **`RSLT001: This Result is discarded`** (a warning in Debug, an error in Release and CI). A statement
  computed a `Result`, a `Result<T>` or an ASP.NET Core Identity `IdentityResult` and dropped it
  (ADR-0030): `invoice.Issue(now);` before `return Result.Success();` commits a transition the aggregate
  refused, and `await handler.Handle(command, ct);` in a test never checks the outcome. In this order:
  use it (return it, `Bind` or `Map` it into the method's own result, or branch on `IsFailure`); in a
  test, assert it (`(await handler.Handle(command, ct)).ShouldBeSuccess()`, or `result.Error.ShouldBe(...)`
  for an expected failure; an Identity call in a fixture goes through `IdentitySeed.EnsureSucceeded`);
  drop it on purpose only when nothing can act on it, as `_ = …;` with a one-line comment saying why.
  Never a `#pragma` or a severity line in `.editorconfig`. The same applies to a dropped `Error` (a
  forgotten `return`), the UI's `ApiResult<T>` in a `.razor` `@code` block, and an array or tuple of
  results (`await Task.WhenAll(...)` over handlers).
- **`RSLT001: This Task<Result> is converted to Task, which drops its result`**. A member, lambda,
  variable or argument typed `Task` was handed a `Task<Result>`: `public Task Handle(…) =>
  port.PushAsync(…);` finishes "successfully" when the push failed. Keep the `Task<Result>` (change the
  return type), or make the method `async`, await the call and act on the `Result` (an outbox handler
  throws on a failure it wants retried).
- **`CS9057` naming the analyzer** ("references version … of the compiler, which is newer than the
  currently running version"): the compiler is older than the Roslyn `tools/Trellis.Analyzers`
  references. Build with the SDK `global.json` names (`dotnet --version` from the repository root
  shows which one runs); an IDE that hosts an older Roslyn only loses the squiggle, while the
  command-line build still enforces the rule. *Bumping the SDK* below says how far the pin may move.

## When a request fails

Every problem response carries `traceId` and `code`. Start from the `traceId`, not the `correlationId`,
which may be a value the caller chose: in the Aspire dashboard (or your OTLP backend) it finds the
request's trace, the use-case span with `trellis.usecase.outcome`, `trellis.usecase.error_code` and
`trellis.usecase.error_type`, and the outcome line logged inside it (events 1001–1006, `docs/deployment.md`
has the telemetry reference). Then let the `code` name the stage that refused the request (the stage
table is in [architecture.md](architecture.md#a-request-stage-by-stage)):

| `code` | Refused by |
|---|---|
| `Http.*` | the host, before any endpoint: an unknown route, a wrong method, the 30 s request timeout |
| `Request.*` | binding: unreadable JSON, a value of the wrong type (`errors[]` names the field), a body too large or not JSON |
| `Auth.Unauthenticated`, `Auth.Forbidden`, `RateLimit.Exceeded` | authentication, authorization, rate limiting; the other `Auth.*` codes are the Auth use cases' own |
| `Idempotency.*` | the idempotency filter; `Idempotency.Duplicate` also comes from the commit, when two first requests raced |
| `Validation.General` | a validator, or a value object the handler attributed with `AtField`: each `errors[]` entry's own `code` says which (`Validation.NotEmpty` against `EmailAddress.Invalid`) |
| `Concurrency.Conflict` | the commit: someone else changed the aggregate since it was loaded |
| `Invoice.*`, `Money.*`, `EmailAddress.*`, any feature's area | the handler and the aggregate: the feature doc's code list says what each means |

Four failures do not explain themselves:

- **`Server.Failure` (500).** The body is opaque on purpose (ADR-0024). Under the same `traceId` look for
  the Error line `Use case … threw` (event 1006) with the exception; without one, the fault happened
  outside a use case (binding, a filter, work after the commit) and the exception handler logged it.
- **The migration job exits 1.** It logs why: a `Seed` value refused at boot, the default admin password
  outside Development, or a scoped service it cannot build, such as a new port with no system stand-in
  for a host without a request ([deployment.md](deployment.md#migrations-a-one-shot-pre-deploy-job), and
  the MigrationService rules in `.claude/rules/tools.md`).
- **A retry keeps answering 409 `Idempotency.InFlight`.** The first request committed and the process
  died before it stored the response; the command did apply. Read the resource, or see
  [Idempotency keys](deployment.md#idempotency-keys) for when the key frees itself.
- **A reaction has not happened** (no receipt, no metric). The event waits in `OutboxMessages`: in
  back-off after a failure (`Attempts`, `Error`), leased by a processor, or dead-lettered;
  [Outbox operations](deployment.md#outbox-operations) reads the row and re-drives it.

## Vulnerable packages (NuGetAudit)

Restore audits the whole package graph, transitive packages included, against the GitHub Advisory
Database (`NuGetAudit`, `NuGetAuditMode` `all`, `NuGetAuditLevel` `low` in `Directory.Build.props`).
An advisory surfaces as warning NU1901–NU1904 (low to critical), which Release and CI builds treat
as an error, so an advisory published overnight can turn a green `main` red with no code change.
That is the point; respond in this order:

1. **Find the path.** The warning names the package and links the advisory;
   `dotnet nuget why Trellis.slnx <package>` shows which reference pulls it in.
2. **Upgrade** the direct reference in `Directory.Packages.props` when a newer version of it (or of
   the package that depends on the vulnerable one) brings the fix.
3. **Pin the transitive package** otherwise: a `<PackageVersion Include="<package>" Version="<patched>" />`
   in `Directory.Packages.props` with a comment naming the advisory (the `Microsoft.OpenApi` entry
   is the worked example). `CentralPackageTransitivePinningEnabled` applies the pin to the
   transitive reference; delete it once the parent package depends on a fixed version itself.
4. **Suppress only when no fixed version exists**, or the vulnerable code provably cannot be
   reached: `<NuGetAuditSuppress Include="https://github.com/advisories/GHSA-xxxx-xxxx-xxxx" />` in an
   `ItemGroup` of `Directory.Build.props`, with a comment saying why it does not apply here and when
   to look again. One advisory per entry, never a whole package.
5. **Raise the level** (`NuGetAuditLevel` to `moderate`) only as a recorded decision (an ADR) for a
   project that reviews low-severity advisories on a schedule rather than on the build that finds
   them; keep `NuGetAuditMode` `all`. The template's own CI stays at `low` + `all`.

NU1900 is different: the advisory data could not be downloaded (offline, feed outage). It is not
an advisory; retry once the feed is reachable.

## Bumping the SDK

`global.json` pins the SDK with `rollForward: latestFeature`, and Dependabot proposes its feature-band
and patch updates (a new major version is a migration, not a bump). With a bump:

- CI installs exactly the version in `global.json`; a contributor needs that version or a later
  feature band of the same major.
- The SDK image (`scripts/sdk-image/Dockerfile`, pinned by digest and moved by Dependabot's docker
  updates) must carry an SDK that `global.json` accepts, or `./scripts/dotnet.sh` cannot build.
- `Microsoft.CodeAnalysis.CSharp` in `Directory.Packages.props` may be raised up to the new compiler's
  version, never above (ADR-0030): the analyzer project references it, and a compiler older than the
  referenced version cannot load the analyzer. `dotnet <dotnet root>/sdk/<version>/Roslyn/bincore/csc.dll -version`
  prints the compiler's version. Raising it is optional.

A new .NET major moves more than the SDK: the target framework, the SDK image's tag, and every package
numbered after .NET (`Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore*`, `Microsoft.Extensions.*`,
the Npgsql EF Core provider, `dotnet-ef`, `Asp.Versioning.Http`, `Serilog.AspNetCore`). Dependabot
proposes none of those majors (`.github/dependabot.yml` ignores them, as it ignores the SDK's), so the
move is one change you make on purpose; their patches and minors keep arriving before and after it.

## Conventions

Layer rules live in `.claude/rules/` (they also guide AI agents). The short version: dependencies point inward, failures are `Result`s not exceptions, migrations are generated not hand-edited, and no commercially-licensed packages (see the ceilings in `Directory.Packages.props`).
