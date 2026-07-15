# Changelog

All notable changes to XTemplate are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Rich-DDD Clean Architecture .NET 10 solution: SharedKernel, Domain, Application,
  Infrastructure ∥ Persistence, Api (+ Common/Contracts), Blazor UI (+ Common),
  Aspire AppHost + MigrationService.
- Hand-rolled CQRS + Result, EF Core 10 with audit/soft-delete/domain-event
  interceptors, ASP.NET Identity + JWT + permission authorization, HybridCache.
- Full test pyramid (unit, architecture, integration via Testcontainers, E2E via
  Aspire.Hosting.Testing) and CI (format, warnings-as-errors, docs-drift).
- `dotnet new xtemplate` templatization with toggles: `IncludeUI`, `UseRedis`,
  `UseSerilog`, `skipRestore`.
- AI-friendly repo: AGENTS.md/CLAUDE.md, `.claude` rules/agents/skills/hooks,
  `.mcp.json`, dual human/AI docs, 17 ADRs.

[Unreleased]: https://github.com/your-org/xtemplate/commits/main
