# Development

## Prerequisites

- **Docker** (Desktop/OrbStack). All build/test runs through the SDK container — no local .NET SDK required for the inner loop.
- A **local .NET 10 SDK** only if you want `aspire run` (the full orchestrated app; ADR-0008).

One-time: build the SDK image (base SDK + docker CLI, needed by Testcontainers/Aspire):

```bash
docker build -t trellis-sdk:10.0 scripts/sdk-image
```

## IDE: open in the dev container (recommended)

This repo is **Docker-only** — there is no .NET SDK on the host, so an IDE cannot resolve types
locally. `.devcontainer/devcontainer.json` fixes that by running the IDE backend *inside* the same
SDK image the scripts use.

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
the `source=` of that mount in `.devcontainer/devcontainer.json` to your socket. Only the
Docker Desktop path is exercised by this repository's own runs; the others follow the engines'
and Testcontainers' documentation.

## Secrets

`Authentication:SigningKey` gates API startup — missing or under 32 characters and the API
refuses to boot. `appsettings.Development.json` ships a dev-only key so F5 works out of the box;
to use your own, set a user secret (the API csproj has a `UserSecretsId`):

```bash
dotnet user-secrets set "Authentication:SigningKey" "<random 32+ chars>" \
    --project src/Presentation/Api/Trellis.Api
```

Run it where the app runs (dev container or local SDK) — user secrets live under that
environment's home directory, so setting them through the ephemeral `./scripts/dotnet.sh`
container does not persist. Never commit a real key; production takes it from the
environment/secret store (see [deployment.md](deployment.md)).

## Running the app

Seeded admin for every option below: `admin@trellis.dev` / `Admin123!`.

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
./scripts/ef.sh migrations remove --project src/Infrastructure/Trellis.Persistence
./scripts/ef.sh migrations add InitialCreate --project src/Infrastructure/Trellis.Persistence
```

## UI tests (optional)

Generate with `--include-ui-tests` to add `tests/Trellis.UiTests` (Playwright). The project always
builds, but the browser tests are **opt-in at run time** — they need real browsers and a running app:

```bash
aspire run                                    # in another terminal
pwsh artifacts/bin/Trellis.UiTests/debug/playwright.ps1 install chromium
RUN_UI_TESTS=1 dotnet test tests/Trellis.UiTests
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
