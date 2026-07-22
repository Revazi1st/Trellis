# Development

## Prerequisites

- **Docker** (Desktop/OrbStack). All build/test runs through the SDK container — no local .NET SDK required for the inner loop.
- A **local .NET 10 SDK** only if you want `aspire run` (the full orchestrated app; ADR-0008).

One-time: build the SDK image (base SDK + docker CLI, needed by Testcontainers/Aspire):

```bash
docker build -t xtemplate-sdk:10.0 scripts/sdk-image
```

## Everyday commands

```bash
./scripts/dotnet.sh build XTemplate.slnx            # build
./scripts/dotnet.sh test XTemplate.slnx             # all tests (Aspire E2E skips unless RUN_ASPIRE_E2E=1)
./scripts/dotnet.sh format XTemplate.slnx           # apply formatting
./scripts/ef.sh migrations add <Name> --project src/Infrastructure/XTemplate.Persistence
```

## Running the app

- **With a local SDK**: `aspire run` — brings up PostgreSQL, the migration worker, the API (Scalar at `/scalar/v1`), and the Blazor web app, with the Aspire dashboard. Seeded admin: `admin@xtemplate.dev` / `Admin123!`.
- **Containerized smoke**: see the `run-app` skill — run Postgres + migration worker + API (+ web) as sibling containers on one network.

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
