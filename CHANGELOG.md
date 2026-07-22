# Changelog

All notable changes to XTemplate are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed
- **Template package was unusable.** `dotnet pack` flattened the whole repo into `content/`
  (293 entries, none nested), dropped colliding files (`NU5118`) and omitted
  `.template.config/`, so `dotnet new install XTemplate.Templates` could not work at all.
  Packing now preserves the tree via `PackagePath="content\%(RecursiveDir)…"`.
- **Project names containing a dot produced a solution that did not compile.** `AddXTemplateUi`
  became `AddAcme.ShopUi`, and Aspire's `Projects.XTemplate_Api` became `Projects.Acme.Shop_Api`.
  The DI extension is renamed `AddSharedUi` (name-independent, like every sibling), and derived
  symbols now map the name to Aspire's `_`/`-` forms for project classes and resource names.
- `.template.config/` no longer ships into generated projects.
- `scripts/check-adr-links.sh` is now made executable by the post-action; CI runs it.

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
