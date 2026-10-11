# ADR-0009: Test stack and the architecture-fitness suite

- **Status**: accepted
- **Date**: 2026-07-15
- **Amended**: 2026-10-11 — the *Test data* row is withdrawn: Bogus is gone, and test data comes from hand-written factories and use-case drivers whose every `Result` is checked (see *Amendment (2026-10-11)* below)
- **Prior art**: [clean-architecture](https://github.com/amantinband/clean-architecture) by Amichai Mantinband (MIT): use-case drivers, optional-parameter factories, the paired HTTP test client and the switchable current-user double, rewritten without a mediator (the 2026-10-11 amendment)

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

*(Amended 2026-10-11: the Test data row no longer holds. No test ever used Bogus; it was removed. See below.)*

The pyramid: `Domain.UnitTests` (pure invariants) · `Application.UnitTests` (handlers + decorator pipeline through the real DI container, faked ports) · `ArchitectureTests` (layer rules and conventions) · `IntegrationTests` (real Postgres) · `ApiTests` (the in-process HTTP pipeline via `WebApplicationFactory` + Testcontainers, contract-serialization guards, the UI clients under `ApiTests/Ui`, and the opt-in Aspire E2E) · `UiTests` (Playwright, only with `--include-ui-tests`, opt-in at run time).

**ArchitectureTests** enforce: SharedKernel depends on no other layer; Domain only on SharedKernel; Application not on Infrastructure/Persistence/EF/ASP.NET; **Infrastructure ∤ Persistence and Persistence ∤ Infrastructure** (the peer rule); no inner layer on Presentation; Api.Contracts is dependency-free; command/query handlers are sealed and internal.

## Consequences

- Every layering violation fails a test with the offending type names — the divergent structure can't silently erode.
- The whole stack is free for commercial use; the license-ceiling policy (ADR-0011, encoded in `Directory.Packages.props`) keeps it that way.
- Analyzers (SonarAnalyzer.CSharp, Meziantou.Analyzer) run at build with a curated `.editorconfig`; `dotnet format --verify-no-changes` is a CI gate.

## Amendment (2026-10-11): test data from factories and drivers, not Bogus

Bogus was referenced by `IntegrationTests` and called by no test. What the suites actually needed was
the same few valid invoices, rebuilt by hand in a dozen files, with the `Result` of each set-up step
often unchecked: a seed that silently failed surfaced later as a puzzling assertion. Random "realistic"
values would not have helped; they make a failure depend on a seed and hide the one value a test is
about. So the package reference and its central version are removed, and test data has three
hand-written seams instead:

- **Factories.** `tests/TestSupport/Invoices/InvoiceFactory.cs` builds valid commands and aggregates,
  with an optional parameter for every value. A test passes what it depends on at the call site and
  leaves the rest to the defaults, so a change to `Invoice.Create` lands in one file.
- **Drivers.** `InvoiceDriver` (IntegrationTests) runs each use case through the real handler and
  pipeline as an `IServiceProvider` extension, asserts success and returns the id; `InvoiceHttpDriver`
  (ApiTests) is the same steps over HTTP. Both are for arrange steps only, one method per use case: no
  generic `SendAsync`, which would be a mediator by another name (ADR-0003). `TestCurrentUser` is the
  one mutable `ICurrentUser` of the subcutaneous hosts, so a test can act as a second user.
- **Checked results.** `ShouldBeSuccess()` (`tests/TestSupport/ResultAssertions.cs`) asserts a `Result`
  succeeded and otherwise names the error's code, type, description and field errors; every arrange
  step ends in it. RSLT001 (ADR-0030) covers tests as well as `src/` and `tools/`, so a test that drops
  a `Result`, an awaited one included, fails the Release build like production code does.

The `tests/TestSupport/` files are linked into the test projects that compile them (each csproj names
them); there is still no shared test project. A feature copied from the Invoices slice gets its own
factory and driver beside these; the Invoice ones are part of the slice and leave with it.
