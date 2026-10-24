# Trellis — Agent Instructions

Trellis is a rich-DDD, Clean Architecture **.NET 10** solution. Vertical-slice features inside layered projects, grouped by ring (`src/Core`, `src/Infrastructure`, `src/Presentation`, `src/Shared`).
<!--#if (TemplateSource) -->

This repository is also the `dotnet new` template that generates such solutions (`.template.config/`, packed from `eng/Trellis.TemplatePack`). Template work has its own tools: the `add-toggle` skill, the source-only section of `.claude/rules/tools.md`, `scripts/check-template-toggles.sh`, `scripts/test-template.sh` (Template CI's legs on your machine) and `docs/releasing.md`. Files a generated project must not inherit as they are (README, CHANGELOG, the governance files) are excluded in `template.json`; the project gets the stubs in `.template.config/consumer/` instead.
<!--#endif -->

## Commands

Build, unit/architecture tests, and Testcontainers integration tests run through the SDK container — **no local .NET SDK needed** (`scripts/dotnet.sh` uses the `trellis-sdk:10.0` image from `scripts/sdk-image/`):

```bash
docker build -t trellis-sdk:10.0 scripts/sdk-image   # one-time
./scripts/dotnet.sh build Trellis.slnx
./scripts/dotnet.sh test  Trellis.slnx               # Aspire E2E skips unless RUN_ASPIRE_E2E=1
./scripts/dotnet.sh test  tests/Trellis.Application.UnitTests --filter-class '*CommandHandlerTests'   # one project, narrowed (or --filter-method); never filter the .slnx
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

- **CQRS, no mediator**: hand-rolled `ICommand(Handler)`/`IQuery(Handler)` + decorator pipeline (tracing→logging→validation→unit-of-work). Endpoints inject handlers directly.
- **Result, not exceptions**: return `Result`/`Result<T>`; the API maps errors to RFC 9457 problem details. Exceptions are for the truly unexpected only. A discarded `Result` fails the Release build (RSLT001, `tools/Trellis.Analyzers`, ADR-0030); drop one on purpose only with `_ = …` and a one-line reason.
- **Rich domain**: aggregates via `static Create(...) → Result<T>` + private EF ctor; value objects = sealed records mapped as EF complex types; hand-rolled strongly-typed IDs; domain events written to the transactional outbox by a SaveChanges interceptor in the same transaction, then dispatched after commit by `OutboxProcessor` (at-least-once, per-handler checkpoints — ADR-0018, ADR-0023).
- **Reference feature**: `src/Core/Trellis.Application/Features/Invoices` + `docs/features/invoices.md`. Copy it with the `add-feature` skill (a checklist of what the tests enforce); delete it with `remove-feature` once your own feature exists.
- **Reference integration**: calling a third party goes through `src/Infrastructure/Trellis.Infrastructure/ExternalApis/` + `docs/features/exchange-rates.md` (ADR-0020) — typed client behind a port returning `Result`, resilience replaced per client — not added (ServiceDefaults' blanket handler cancels every attempt at 10s), registered by the API only (`AddOutboundIntegrations`), options validated at boot. The `add-integration` skill walks through it.

Layer-specific conventions load automatically from `.claude/rules/` when you touch matching files. The "why" behind decisions is in `docs/adr/` — read `docs/INDEX.md` to find them.

## Boundaries

- **Always**: run build + tests before finishing a change; regenerate `docs/api/openapi.v1.json` after any endpoint or contract change (`UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/Trellis.ApiTests` — `OpenApiSnapshotTests` fails until you do); update `docs/features/<feature>.md` in the same change as anything under that feature's folders (`Features/<Feature>/`, `Domain/<Feature>/`, `Api.Contracts/<Feature>/`, `ExternalApis/<Feature>/`, its EF configurations and UI pages), and `docs/threat-model.md` when authentication, hardening or identity code changes — `scripts/check-docs-drift.sh` enforces both in CI (the edit hook runs the same check).
  - A bug fix starts with a test that fails without the fix.
  - A reviewer subagent's finding (`code-reviewer`, `architecture-guard`) is a claim: check it against the code or a test before acting on it, and push back when it would undo a decision an ADR records.
  - A rule, skill or doc that misled you is corrected in the same change as the work it misled.
- **Ask first**: adding a NuGet package (check the LICENSE CEILINGS block in `Directory.Packages.props`); adding a project.
- **Never**: reference Infrastructure↔Persistence; hand-edit files under `Migrations/`; throw for expected failures instead of returning a `Result`; commit secrets; add MediatR / AutoMapper / FluentAssertions / MassTransit / Moq (the license ceilings — see `Directory.Packages.props`, ADR-0011).
