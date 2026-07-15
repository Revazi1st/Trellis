# XTemplate.Templates

A rich-DDD, Clean Architecture .NET 10 solution template.

```bash
dotnet new install XTemplate.Templates
dotnet new xtemplate -n Your.Project
```

Toggles: `--include-ui` (Blazor web UI, default on), `--use-redis` (Redis HybridCache L2, default off), `--skip-restore`.

Feature-based minimal APIs, separate Infrastructure and Persistence, hand-rolled CQRS + Result, EF Core 10, ASP.NET Identity + JWT, Aspire orchestration, and an AI-friendly repository (CLAUDE.md/AGENTS.md, .claude rules/agents/skills).
