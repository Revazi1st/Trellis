# XTemplate — Agent Instructions

> STUB — completed at milestone M10. Commands and boundaries below are already accurate.

XTemplate is a rich-DDD, Clean Architecture .NET 10 solution (and eventually a `dotnet new` template). Vertical-slice features inside layered projects; Infrastructure and Persistence are peer projects (never reference each other).

## Commands

No local .NET SDK is required — everything runs through the SDK container:

```bash
./scripts/dotnet.sh build XTemplate.slnx        # build everything
./scripts/dotnet.sh test XTemplate.slnx         # run all tests
./scripts/dotnet.sh restore XTemplate.slnx
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
