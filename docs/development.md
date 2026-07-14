# Development

## Prerequisites

- **Docker** (Desktop/OrbStack). All build/test runs through the SDK container — no local .NET SDK required for the inner loop.
- A **local .NET 10 SDK** only if you want `aspire run` (full app) or to build the deferred MAUI head (ADR-0008).

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

## Tests

| Project | What it needs |
|---|---|
| Domain/Application UnitTests | nothing (in-memory) |
| IntegrationTests | Docker (Testcontainers spins up PostgreSQL) |
| ArchitectureTests | nothing |
| ApiTests | `RUN_ASPIRE_E2E=1` + a local SDK or CI (Aspire orchestration) — otherwise the E2E skips; contract tests always run |

## Conventions

Layer rules live in `.claude/rules/` (they also guide AI agents). The short version: dependencies point inward, failures are `Result`s not exceptions, migrations are generated not hand-edited, and no commercially-licensed packages (see the ceilings in `Directory.Packages.props`).
