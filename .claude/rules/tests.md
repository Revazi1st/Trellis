---
description: Testing conventions
paths: ["tests/**/*.cs"]
---

# Testing rules

- **xUnit v3** + **Shouldly** (`value.ShouldBe(...)`). Mocks: **NSubstitute**. Never FluentAssertions or Moq.
- Test names: `Method_Scenario_Outcome`.
- **Domain.UnitTests**: pure, no mocks. **Application.UnitTests**: handlers/decorators with faked ports (`Substitute.For<...>`), `FakeTimeProvider` for time.
- **IntegrationTests**: real PostgreSQL via `IntegrationTestBase` (Testcontainers + Respawn) — exercise the real Application pipeline against a real DB. **Do not mock EF or the DbContext.**
- **ArchitectureTests**: add a rule here when you introduce a new layer boundary.
- **ApiTests**: E2E via Aspire (opt-in `RUN_ASPIRE_E2E=1`) + always-on contract-serialization guards.
- Use `TestContext.Current.CancellationToken` for cancellable calls.
