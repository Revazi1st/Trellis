# Trellis — Agent Instructions

Trellis is a rich-DDD, Clean Architecture **.NET 10** solution and a `dotnet new` template (packed from `eng/Trellis.TemplatePack`). Vertical-slice features inside layered projects, grouped by ring (`src/Core`, `src/Infrastructure`, `src/Presentation`, `src/Shared`).

## Commands

Build, unit/architecture tests, and Testcontainers integration tests run through the SDK container — **no local .NET SDK needed** (`scripts/dotnet.sh` uses the `trellis-sdk:10.0` image from `scripts/sdk-image/`):

```bash
docker build -t trellis-sdk:10.0 scripts/sdk-image   # one-time
./scripts/dotnet.sh build Trellis.slnx
./scripts/dotnet.sh test  Trellis.slnx               # Aspire E2E skips unless RUN_ASPIRE_E2E=1
./scripts/dotnet.sh format Trellis.slnx
./scripts/ef.sh migrations add <Name> --project src/Infrastructure/Trellis.Persistence
```

**Aspire orchestration needs a real host, not the container** (ADR-0008): `aspire run` and the `Aspire.Hosting.Testing` E2E test need a local .NET 10 SDK or CI. `aspire run` brings up postgres + migrations + api + web. Seeded admin: `admin@example.com` / `Admin123!`.

## Dependency flow (architecture-test enforced)

```
SharedKernel ← Domain ← Application ← { Infrastructure ∥ Persistence } ← Api → Api.Common → Api.Contracts
Api → Api.Contracts ← UI.Common ;  UI.Web → UI.Common
```

Dependencies point inward. **Infrastructure and Persistence are peers — never reference each other** (ADR-0002; agents trained on mainstream templates expect a single Infrastructure project — this one splits them).

## How we build things

- **CQRS, no mediator**: hand-rolled `ICommand(Handler)`/`IQuery(Handler)` + decorator pipeline (logging→tracing→validation→unit-of-work). Endpoints inject handlers directly.
- **Result, not exceptions**: return `Result`/`Result<T>`; the API maps errors to RFC 9457 problem details. Exceptions are for the truly unexpected only.
- **Rich domain**: aggregates via `static Create(...) → Result<T>` + private EF ctor; value objects = sealed records mapped as EF complex types; hand-rolled strongly-typed IDs; domain events dispatched by a SaveChanges interceptor.
- **Reference feature**: `src/Core/Trellis.Application/Features/Invoices` + `docs/features/invoices.md`. Copy it (use the `add-feature` skill).
- **Reference integration**: calling a third party goes through `src/Infrastructure/Trellis.Infrastructure/ExternalApis/` + `docs/features/exchange-rates.md` (ADR-0020) — typed client behind a port returning `Result`, resilience replaced per client — not added (ServiceDefaults' blanket handler cancels every attempt at 10s), registered by the API only (`AddOutboundIntegrations`), options validated at boot.

Layer-specific conventions load automatically from `.claude/rules/` when you touch matching files. The "why" behind decisions is in `docs/adr/` — read `docs/INDEX.md` to find them.

## Boundaries

- **Always**: run build + tests before finishing a change; update `docs/features/<x>.md` when a feature's behavior changes, and `docs/threat-model.md` when authentication, hardening or identity code changes (CI enforces both).
- **Ask first**: adding a NuGet package (check the LICENSE CEILINGS block in `Directory.Packages.props`); adding a project.
- **Never**: reference Infrastructure↔Persistence; hand-edit files under `Migrations/`; throw for expected failures instead of returning a `Result`; commit secrets; add MediatR / AutoMapper / FluentAssertions / MassTransit (commercial — see `Directory.Packages.props`).
