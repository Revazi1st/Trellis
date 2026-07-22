# Development

## Prerequisites

- **Docker** (Desktop/OrbStack). All build/test runs through the SDK container — no local .NET SDK required for the inner loop.
- A **local .NET 10 SDK** only if you want `aspire run` (the full orchestrated app; ADR-0008).

One-time: build the SDK image (base SDK + docker CLI, needed by Testcontainers/Aspire):

```bash
docker build -t xtemplate-sdk:10.0 scripts/sdk-image
```

## IDE: open in the dev container (recommended)

This repo is **Docker-only** — there is no .NET SDK on the host, so an IDE cannot resolve types
locally. `.devcontainer/devcontainer.json` fixes that by running the IDE backend *inside* the same
SDK image the scripts use.

**Rider**: open the solution → Rider detects `.devcontainer` and offers *"Reopen in Dev Container"*
(or use **File → Remote Development → Dev Containers**). VS Code: *Dev Containers: Reopen in Container*.

You then get full IntelliSense, build, debug, and test in the IDE, while nothing is installed on the
host. The container mounts the Docker socket, so Testcontainers integration tests and the Aspire
orchestrator work from inside it too, and it shares the `xtemplate-nuget` package volume with
`./scripts/dotnet.sh` (same `/workspace` path, so build output stays valid across both).

> Without the dev container the IDE will show unresolved-type errors for the whole BCL. That is
> expected on a host with no SDK — it is not a problem with the code.

## Everyday commands

```bash
./scripts/dotnet.sh build XTemplate.slnx            # build
./scripts/dotnet.sh test XTemplate.slnx             # all tests (Aspire E2E skips unless RUN_ASPIRE_E2E=1)
./scripts/dotnet.sh format XTemplate.slnx           # apply formatting
./scripts/ef.sh migrations add <Name> --project src/Infrastructure/XTemplate.Persistence
```

## Running the app

Seeded admin for every option below: `admin@xtemplate.dev` / `Admin123!`.

### From the IDE, in the dev container (no local SDK)

`aspire run` **cannot** drive the app from inside a container — the orchestrator would run the
projects in the container while starting PostgreSQL as a sibling on the host, and the two cannot
reach each other (ADR-0008). Run the dependencies with Compose and the projects from the IDE:

```bash
docker compose up -d          # PostgreSQL on host port 5433
```

Then in Rider/VS Code, using the **`http (dev container)`** launch profiles — they point at
`host.docker.internal:5433`, because inside the dev container `localhost` is the container itself:

1. Run **XTemplate.MigrationService** (profile `migrate (dev container)`) once — applies migrations and seeds.
2. Run **XTemplate.Api** (profile `http (dev container)`) → <http://localhost:5171>, Scalar at `/scalar/v1`.
3. Run **XTemplate.UI.Web** (profile `http (dev container)`) → <http://localhost:5170>.

Ports 5170/5171 are in `forwardPorts`, so they open in the host browser. Step 3 sets
`Services__api__http__0`, which stands in for the Aspire service-discovery reference the UI uses
for the API (`https+http://api`) — nothing resolves that outside Aspire.

> Port **5433**, not 5432: a native PostgreSQL install commonly owns 5432, and the clash fails
> confusingly — it connects, then rejects the password. Override with `POSTGRES_PORT` and match it
> in the launch profile. Redis (for `--use-redis`) is behind a profile: `docker compose --profile redis up -d`.

### With a local SDK

`aspire run` — PostgreSQL, the migration worker, the API, the Blazor web app, and the Aspire
dashboard, all wired together. This is the best loop when a host SDK is available.

### Containerized smoke

See the `run-app` skill — Postgres + migration worker + API (+ web) as sibling containers on one network.

## Multi-tenancy (optional)

Generate with `--use-multitenancy` to add the tenant seam: `ITenantContext` (resolved from the
`tenant` claim, falling back to the `X-Tenant-Id` header), a `TenantId` shadow column stamped by
`TenantInterceptor`, and a named `"Tenant"` EF query filter (opt out with
`IgnoreQueryFilters(["Tenant"])`). See ADR-0019.

Because this adds a column, **regenerate the initial migration** in the generated project:

```bash
./scripts/ef.sh migrations remove --project src/Infrastructure/XTemplate.Persistence
./scripts/ef.sh migrations add InitialCreate --project src/Infrastructure/XTemplate.Persistence
```

## UI tests (optional)

Generate with `--include-ui-tests` to add `tests/XTemplate.UiTests` (Playwright). The project always
builds, but the browser tests are **opt-in at run time** — they need real browsers and a running app:

```bash
aspire run                                    # in another terminal
pwsh artifacts/bin/XTemplate.UiTests/debug/playwright.ps1 install chromium
RUN_UI_TESTS=1 dotnet test tests/XTemplate.UiTests
```

Without `RUN_UI_TESTS=1` they skip, so the normal suite stays fast and browser-free
(the same opt-in pattern as the Aspire E2E test).

## Tests

| Project | What it needs |
|---|---|
| Domain/Application UnitTests | nothing (in-memory) |
| IntegrationTests | Docker (Testcontainers spins up PostgreSQL) |
| ArchitectureTests | nothing |
| ApiTests | `RUN_ASPIRE_E2E=1` + a local SDK or CI (Aspire orchestration) — otherwise the E2E skips; contract tests always run |
| UiTests (optional) | `RUN_UI_TESTS=1` + installed browsers + a running app — otherwise the browser test skips |

## Conventions

Layer rules live in `.claude/rules/` (they also guide AI agents). The short version: dependencies point inward, failures are `Result`s not exceptions, migrations are generated not hand-edited, and no commercially-licensed packages (see the ceilings in `Directory.Packages.props`).
