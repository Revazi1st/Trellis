# Docs Index

One line per document — agents: start here, read only what the task needs.

- [adr/0001-record-architecture-decisions.md](adr/0001-record-architecture-decisions.md) — why and how we write ADRs
- [adr/0002-infrastructure-persistence-split.md](adr/0002-infrastructure-persistence-split.md) — Infrastructure ∥ Persistence as peer projects (diverges from mainstream templates)
- [adr/0007-identity-in-persistence-custom-jwt-endpoints.md](adr/0007-identity-in-persistence-custom-jwt-endpoints.md) — Identity in Persistence, JWT minting in Infrastructure, custom auth endpoints, permission-based authorization
- [adr/0008-containerized-sdk-and-aspire-orchestration.md](adr/0008-containerized-sdk-and-aspire-orchestration.md) — containerized SDK for build/test; Aspire run + E2E need a local SDK or CI (RUN_ASPIRE_E2E=1)
- [adr/0017-hand-rolled-specifications.md](adr/0017-hand-rolled-specifications.md) — minimal Specification&lt;T&gt; in SharedKernel; Domain stays dependency-free; no Includes (aggregates load whole)

<!-- architecture.md, development.md, features/*.md arrive with their milestones (M4+, completed at M10). -->
