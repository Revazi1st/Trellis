# ADR-0009: Test stack and the architecture-fitness suite

- **Status**: accepted
- **Date**: 2026-07-15

## Context

The template needs a full test pyramid whose libraries are all free for commercial use (the 2025–26 licensing wave pushed several defaults — FluentAssertions v8, MassTransit v9 — behind paid licenses; see ADR-0011). It also needs the layering rules to be *executable*, not just documented, because the structure deliberately diverges from mainstream templates (ADR-0002).

## Decision

| Concern | Choice | Why |
|---|---|---|
| Test framework | **xUnit v3** (`xunit.v3`) on Microsoft.Testing.Platform | Best ecosystem + LLM familiarity; MTP is the .NET 10 runner (`global.json` selects it) |
| Assertions | **Shouldly** (BSD-3) | Free; avoids FluentAssertions v8 (commercial) |
| Mocking | **NSubstitute** (BSD-3) | Free, active; avoids Moq's SponsorLink history |
| Integration DB | **Testcontainers** + **Respawn** | Real PostgreSQL per collection, Respawn resets between tests — no EF mocking |
| Test data | **Bogus** | Realistic fixtures |
| Clock | **FakeTimeProvider** | Deterministic time (pairs with `TimeProvider` in product code) |
| E2E | **Aspire.Hosting.Testing** | Boots the real AppHost; opt-in via `RUN_ASPIRE_E2E=1` (ADR-0008) |
| Architecture | **NetArchTest.eNhancedEdition** | Maintained fork (original NetArchTest.Rules is dead since 2021); simple fluent API, xUnit-v3-agnostic |

The pyramid: `Domain.UnitTests` (pure invariants) · `Application.UnitTests` (handlers + decorator pipeline through the real DI container, faked ports) · `ArchitectureTests` (layer rules) · `IntegrationTests` (real Postgres) · `ApiTests` (E2E + always-on contract-serialization guards).

**ArchitectureTests** enforce: SharedKernel depends on no other layer; Domain only on SharedKernel; Application not on Infrastructure/Persistence/EF/ASP.NET; **Infrastructure ∤ Persistence and Persistence ∤ Infrastructure** (the peer rule); no inner layer on Presentation; Api.Contracts is dependency-free; command/query handlers are sealed and internal.

## Consequences

- Every layering violation fails a test with the offending type names — the divergent structure can't silently erode.
- The whole stack is free for commercial use; the license-ceiling policy (ADR-0011, encoded in `Directory.Packages.props`) keeps it that way.
- Analyzers (SonarAnalyzer.CSharp, Meziantou.Analyzer) run at build with a curated `.editorconfig`; `dotnet format --verify-no-changes` is a CI gate.
