# ADR-0002: Infrastructure and Persistence are separate peer projects

- **Status**: accepted
- **Date**: 2026-07-14

## Context

Mainstream Clean Architecture templates (Jason Taylor, Ardalis, SSW as of 2026) ship a single `Infrastructure` project with a `Persistence` *folder* inside it. That conflates two adapters with very different change cadence, dependency weight, and test strategy: database access (EF Core, migrations, interceptors — heavy, integration-tested) versus everything else (token minting, email, clock — light, unit-testable).

## Decision

`Trellis.Infrastructure` and `Trellis.Persistence` are **separate projects under `src/Infrastructure/`, and strict peers**: neither references the other. Both reference `Trellis.Application` and implement its ports.

- Persistence owns: `AppDbContext` (+ ASP.NET Core Identity stores), entity configurations, interceptors (auditing, soft-delete, and the domain-event outbox write that replaced in-transaction dispatch, ADR-0018), query filters, repositories/specification evaluator, migrations, seeding, `IIdentityService`.
- Infrastructure owns: non-persistence adapters — JWT `ITokenProvider`, `IEmailSender`, `TimeProvider` registration.
- The peer rule is enforced by an architecture test (`LayerDependencyTests`).

## Consequences

- Clearer blast radius: an EF major upgrade touches one project; swapping an email provider touches the other.
- One extra project versus mainstream templates; composition roots must register both.
- **Agent warning**: AI models trained on mainstream templates expect `Infrastructure/Persistence` as a folder. `AGENTS.md` and `.claude/rules/` restate this split so agents don't "helpfully" merge them or add cross-references.
