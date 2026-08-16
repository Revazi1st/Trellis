# XTemplate.Templates

A rich-DDD, Clean Architecture .NET 10 solution template.

```bash
dotnet new install XTemplate.Templates
dotnet new xtemplate -n Your.Project
```

Toggles (checkboxes in Visual Studio / Rider, flags on the CLI):

| Flag | Default | What it adds |
|---|---|---|
| `--include-ui` | on | Blazor web UI (UI.Common + UI.Web), orchestrated by Aspire |
| `--use-redis` | off | Redis distributed cache as a HybridCache L2 |
| `--use-serilog` | off | Serilog structured logging (console) alongside OpenTelemetry |
| `--use-multitenancy` | off | Tenant seam: `ITenantContext`, `TenantId` column + `"Tenant"` query filter |
| `--include-ui-tests` | off | Playwright browser tests for the Blazor UI (opt-in at run time) |
| `--skip-restore` | off | Skip the automatic `dotnet restore` |

Feature-based minimal APIs, separate Infrastructure and Persistence, hand-rolled CQRS + Result, EF Core 10, ASP.NET Identity + JWT, Aspire orchestration, and an AI-friendly repository (CLAUDE.md/AGENTS.md, .claude rules/agents/skills).
