# XTemplate — Agent Instructions

> STUB — completed at milestone M10. Commands and boundaries below are already accurate.

XTemplate is a rich-DDD, Clean Architecture .NET 10 solution (and eventually a `dotnet new` template). Vertical-slice features inside layered projects; Infrastructure and Persistence are peer projects (never reference each other).

## Commands

Build, unit/architecture tests, and Testcontainers integration tests need no local .NET SDK — they run through the SDK container (`scripts/dotnet.sh`, which uses the `xtemplate-sdk:10.0` image built from `scripts/sdk-image/`):

```bash
docker build -t xtemplate-sdk:10.0 scripts/sdk-image   # one-time: SDK + docker CLI
./scripts/dotnet.sh build XTemplate.slnx               # build everything
./scripts/dotnet.sh test XTemplate.slnx               # unit + integration tests (E2E skips)
./scripts/ef.sh migrations add <Name> --project src/Infrastructure/XTemplate.Persistence
```

**Aspire orchestration needs a real host, not the SDK container** (ADR-0008): `aspire run`, the AppHost F5 loop, and the `Aspire.Hosting.Testing` E2E test (`XTemplate.ApiTests`) require a **local .NET 10 SDK** or CI. The E2E test is opt-in via `RUN_ASPIRE_E2E=1`; with a local SDK:

```bash
aspire run                                             # whole app (postgres, migrations, api, web) + dashboard
RUN_ASPIRE_E2E=1 dotnet test tests/XTemplate.ApiTests  # end-to-end HTTP flow
```

## Dependency flow (architecture-test enforced)

```
SharedKernel ← Domain ← Application ← { Infrastructure ∥ Persistence } ← Api → Api.Common
Api → Api.Contracts ← UI.Common ;  UI.Web → UI.Common ← UI.Mobile
```

## Boundaries

- **Always**: run build + tests before finishing a change.
- **Ask first**: adding a NuGet package (check the LICENSE CEILINGS block in `Directory.Packages.props`), adding a project.
- **Never**: edit files under `Migrations/` by hand; commit secrets; add MediatR/AutoMapper/FluentAssertions/MassTransit (commercial licenses — see `Directory.Packages.props`).
