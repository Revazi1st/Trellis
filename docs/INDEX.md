# Docs Index

Agent entry point — read only what the task needs.

## Guides (humans & agents)
- [architecture.md](architecture.md) — the rings, the dependency flow, the key patterns
- [development.md](development.md) — prerequisites, commands, running the app, tests
- [features/invoices.md](features/invoices.md) — the canonical "copy me" vertical slice

## Decisions (docs/adr/)
- [0001](adr/0001-record-architecture-decisions.md) — record ADRs
- [0002](adr/0002-infrastructure-persistence-split.md) — Infrastructure ∥ Persistence peer projects
- [0007](adr/0007-identity-in-persistence-custom-jwt-endpoints.md) — Identity in Persistence; custom JWT auth; permission-based authz
- [0008](adr/0008-containerized-sdk-and-aspire-orchestration.md) — containerized SDK for build/test; Aspire run + E2E need local SDK or CI
- [0009](adr/0009-test-stack.md) — test pyramid + architecture-fitness tests; free-for-commercial libraries
- [0013](adr/0013-api-contracts-public-surface.md) — Api.Contracts as the API's public surface
- [0014](adr/0014-ai-documentation-architecture.md) — AI documentation architecture
- [0015](adr/0015-blazor-everywhere-shared-ui.md) — one Razor Class Library for web + mobile
- [0017](adr/0017-hand-rolled-specifications.md) — hand-rolled Specification<T>, Domain stays dependency-free
