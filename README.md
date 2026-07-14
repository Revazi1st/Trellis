# XTemplate

A best-practices .NET 10 solution template: **rich DDD**, **Clean Architecture** with vertical-slice features, **feature-based Minimal APIs**, **Blazor** web + **.NET MAUI Blazor Hybrid** mobile sharing one component library, **Aspire 13** orchestration, and a deeply **AI-friendly** repository (dual human/AI docs, Claude Code rules/skills/hooks).

> 🚧 Under construction — built milestone by milestone. Templatization (`dotnet new xtemplate` with feature toggles) lands last.

## Quick start

```bash
# with a local .NET 10 SDK
dotnet build XTemplate.slnx
dotnet test XTemplate.slnx

# without one (Docker only)
./scripts/dotnet.sh build XTemplate.slnx
./scripts/dotnet.sh test XTemplate.slnx
```

## Solution layout

| Group | Projects | Role |
|---|---|---|
| `src/Core` | SharedKernel, Domain, Application | The inside of the onion — pure C#, no framework deps |
| `src/Infrastructure` | Infrastructure ∥ Persistence | Driven adapters (peers — no cross-reference) |
| `src/Presentation/Api` | Api.Contracts, Api.Common, Api | Feature-based Minimal API host + shared plumbing + public contracts |
| `src/Presentation/UI` | UI.Common, UI.Web, UI.Mobile* | Blazor components shared by web and mobile heads |
| `src/Shared` | ServiceDefaults | Aspire service defaults (OTel, health, discovery, resilience) |
| `tools` | AppHost, MigrationService | Aspire orchestration + DB migrate/seed worker |
| `tests` | 5 projects | Unit, architecture, integration (Testcontainers), E2E (Aspire.Hosting.Testing) |

\* UI.Mobile arrives at milestone M8 and requires `dotnet workload install maui`.

Architecture decisions are recorded in [docs/adr](docs/adr/).
