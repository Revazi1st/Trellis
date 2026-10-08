# Docs Index

Agent entry point — read only what the task needs.

## Guides (humans & agents)
- [architecture.md](architecture.md) — the rings, the dependency flow, the key patterns
- [development.md](development.md) — prerequisites, commands, running the app, tests
- [deployment.md](deployment.md) — container images, the migration job, required production config, probes, OTLP
- [upgrading.md](upgrading.md) — adopting a newer template version: regenerate, diff, merge
- [features/invoices.md](features/invoices.md) — the canonical "copy me" vertical slice
- [features/auth.md](features/auth.md) — Identity + JWT, refresh rotation/revocation, permissions, lockout
- [features/exchange-rates.md](features/exchange-rates.md) — the reference outbound HTTP integration (typed client, per-client resilience, cached probe)
- [threat-model.md](threat-model.md) — assets, mitigations that exist today, known gaps to decide before production
<!--#if (TemplateSource) -->
- [releasing.md](releasing.md) — releasing the template package: CHANGELOG section, dry run, tag, nuget.org trusted publishing, recovery
<!--#endif -->

## Decisions (docs/adr/)
- [0001](adr/0001-record-architecture-decisions.md) — record ADRs
- [0002](adr/0002-infrastructure-persistence-split.md) — Infrastructure ∥ Persistence peer projects
- [0003](adr/0003-hand-rolled-cqrs.md) — hand-rolled CQRS handlers + decorator pipeline (no mediator)
- [0004](adr/0004-result-over-exceptions.md) — Result pattern over exceptions; ProblemDetails mapping
- [0005](adr/0005-hand-rolled-strongly-typed-ids.md) — hand-rolled strongly-typed IDs (Vogen alternative)
- [0006](adr/0006-ef-complex-types-and-interceptors.md) — EF 10 complex types for value objects + SaveChanges interceptors
- [0007](adr/0007-identity-in-persistence-custom-jwt-endpoints.md) — Identity in Persistence; custom JWT auth; permission-based authz
- [0008](adr/0008-containerized-sdk-and-aspire-orchestration.md) — containerized SDK for build/test; Aspire run + E2E need local SDK or CI
- [0009](adr/0009-test-stack.md) — test pyramid + architecture-fitness tests; free-for-commercial libraries
- [0010](adr/0010-slnx-conditional-templating.md) — conditional projects in the .slnx template (XML-comment conditionals work in .NET 10)
- [0011](adr/0011-license-ceiling-policy.md) — license-ceiling policy for encumbered packages
- [0012](adr/0012-presentation-structure-common-libraries.md) — Presentation split into Api and UI, each with a Common library
- [0013](adr/0013-api-contracts-public-surface.md) — Api.Contracts as the API's public surface
- [0014](adr/0014-ai-documentation-architecture.md) — AI documentation architecture
- [0015](adr/0015-blazor-everywhere-shared-ui.md) — shared render-mode-agnostic Razor Class Library for the Blazor UI
- [0016](adr/0016-ring-grouped-folders.md) — ring-grouped source folders (Core / Infrastructure / Presentation / Shared)
- [0017](adr/0017-hand-rolled-specifications.md) — hand-rolled Specification<T>, Domain stays dependency-free
- [0018](adr/0018-transactional-outbox-and-idempotency.md) — transactional outbox for domain events + command idempotency
- [0019](adr/0019-multitenancy-toggle.md) — multi-tenancy as an opt-in template toggle
- [0020](adr/0020-outbound-http-integrations.md) — outbound HTTP integrations: typed client, per-client resilience, validated options
- [0021](adr/0021-api-hardening-defaults.md) — API hardening defaults: deny-by-default group + allowlist test, forwarded-header trust never widens, rate limits by credential type, headers, limits
- [0022](adr/0022-tenant-on-the-principal.md) — tenant on the principal: membership on the user, claim-only resolution (no header), fail-closed tokens, convention-based scoping of every aggregate root
- [0023](adr/0023-outbox-lease-and-checkpoints.md) — outbox processor semantics: lease-based claim with dispatch outside the transaction, per-handler checkpoints, token-keyed cancellation, validated `Outbox` options, backlog gauges, SQL re-drive runbook
- [0024](adr/0024-problem-details-v2.md) — problem details v2: `code`/`type`/`correlationId`/`instance` on every problem, human titles, `errors[]` of `{field, code, description}`, one writer (`CustomResults` + `ProblemCatalog`/`WriteProblemAsync`), status-code pages, malformed requests as 400 problems
