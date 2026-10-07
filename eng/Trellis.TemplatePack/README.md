# Trellis.Templates

A rich-DDD, Clean Architecture .NET 10 solution template. Like its namesake — the lattice a
gardener puts up for climbing plants — Trellis is the load-bearing frame your application grows
over: it shapes the structure, then fades from view as your features fill it in.

```bash
dotnet new install Trellis.Templates
dotnet new trellis -n Your.Project
```

Name it with dot-separated parts made of letters and digits (`Acme.Shop`): hyphens, spaces and
underscores become underscores in namespaces, which fail analyzer rule CA1707.

Toggles (checkboxes in Visual Studio / Rider, flags on the CLI):

| Flag | Default | What it adds |
|---|---|---|
| `--include-ui` | on | Blazor web UI (UI.Common + UI.Web), orchestrated by Aspire |
| `--use-redis` | off | Redis distributed cache as a HybridCache L2 |
| `--use-serilog` | off | Serilog structured logging (console) alongside OpenTelemetry |
| `--use-multitenancy` | off | Tenant seam: `tenant` claim on the principal, `TenantId` column + `"Tenant"` query filter on every aggregate root |
| `--include-ui-tests` | off | Playwright browser tests for the Blazor UI (opt-in at run time) |
| `--skip-restore` | off | Skip the automatic `dotnet restore` |

Feature-based minimal APIs, separate Infrastructure and Persistence, hand-rolled CQRS + Result, EF Core 10, ASP.NET Identity + JWT, Aspire orchestration, and an AI-friendly repository (CLAUDE.md/AGENTS.md, .claude rules/agents/skills).
