# ADR-0030: A discarded Result fails the build (RSLT001)

- **Status**: accepted
- **Date**: 2026-10-11
<!--#if (TemplateSource) -->
- **Prior art**: [Trellis](https://github.com/xavierjohn/Trellis) by Xavier John (MIT; no affiliation, the shared name is a coincidence): a build-time analyzer that reports a result nobody handles. The idea only; this analyzer is written from scratch. A generated project would rename that repository's name, so the line stays in the template's repository, beside the README's Credits.
<!--#endif -->

## Context

ADR-0004 makes failure part of the type, and nothing enforced it. `invoice.Issue(now); return Result.Success();`
compiles, commits a transition the aggregate refused, and answers 2xx. An awaited
`await handler.Handle(command, ct);` in a test drops the outcome the test was meant to check, and
ASP.NET Core Identity reports a failed user or role write as an `IdentityResult` value rather than an
exception: a seeding step that dropped one once logged success and left no administrator.

The first guard was CA1806 with `additional_use_results_methods` listing every Domain member and
`Result` combinator by documentation-ID prefix. It matched by name, not by type, so a handler, an
Application port, a Persistence method or an `IdentityResult` was never covered, and it never saw
through `await`, the most common way a `Result` is dropped. When the analyzer below was first wired in,
it found 33 statements that CA1806 had passed, all in tests: 14 NSubstitute call checks and 19 real
drops (identity seeding in the ApiTests and IntegrationTests fixtures that never checked an
`IdentityResult`, a handler test, and the use-case telemetry tests that never looked at the outcome
they caused); the all-options template build then found 3 more in multitenancy-only fixtures.

## Decision

- **An in-repository analyzer**, `tools/Trellis.Analyzers`: one netstandard2.0 Roslyn component
  (`IsRoslynComponent`, `EnforceExtendedAnalyzerRules`, not packable), one analyzer class, one
  diagnostic. **RSLT001**, "The result of this call is discarded", is a Warning by default; the
  Release and CI builds treat warnings as errors, so it fails them. The id is neutral, so the
  template rename leaves it alone.
- **What it reports**: an expression statement whose value is a result and is thrown away: a direct
  call, an awaited `Task<T>` or `ValueTask<T>` of one (through `ConfigureAwait` too), a conditional
  access ending in such a call, an object creation, and the implicit statement of an expression-bodied
  lambda, local function or member that returns void (`Action a = () => invoice.Issue(now);`).
- **What it does not report**: any assignment, so `_ = …` is the way to drop a result on purpose; a
  return; an argument; a lambda whose return type is the result (`Func<Result>`); an awaited
  non-generic `Task`; generated code (the `Migrations/` section of `.editorconfig` sets
  `generated_code`); and NSubstitute's call check (`await uow.Received(1).CommitAsync(ct)`), which
  asserts that a call happened and returns a placeholder, so it drops no outcome.
- **The result types**: the SharedKernel `Result` and `Result<T>`, matched by type name plus a
  containing namespace that ends in `SharedKernel.Results` (so it still matches after the template
  renames the root namespace), together with any type derived from them and a type parameter constrained
  to them; and
  `Microsoft.AspNetCore.Identity.IdentityResult`, by its full name.
- **Wiring**: the root `Directory.Build.props` gives every project under `src/`, `tests/` and `tools/`
  a `ProjectReference` to the analyzer with `OutputItemType="Analyzer"`, `ReferenceOutputAssembly="false"`,
  `PrivateAssets="all"` and `IsAspireProjectResource="false"`. The compiler loads it, no project references
  or copies it, it flows to no consumer, and the AppHost does not take it for a service. The item is
  conditioned on the project name, so the analyzer does not load itself and the condition renames
  with the path. `eng/` has its own empty props and never sees it. `ArchitectureTests` also references
  the assembly, with `Microsoft.CodeAnalysis.CSharp`, to run it over snippets in
  `ResultDiscardAnalyzerTests`.
- **Packages**: `Microsoft.CodeAnalysis.CSharp` 5.0.0 and `Microsoft.CodeAnalysis.Analyzers` 3.11.0,
  both MIT, `PrivateAssets="all"`, pinned in `Directory.Packages.props` (ADR-0029). There is no code-fix
  provider. A fix lives in its own assembly and needs `Microsoft.CodeAnalysis.CSharp.Workspaces`, a third
  package, and the edit it would make is typing `_ = ` or handling the value.
- **The version is a ceiling on the compiler.** A compiler older than the Roslyn an analyzer
  references cannot load it (CS9057, an error in Release), and an IDE that hosts an older Roslyn shows
  no squiggle. A newer compiler loads it, so the pin is the lowest version that restores and has the
  APIs the analyzer uses. The IOperation APIs have been stable for years, and 4.14.0 was the
  intended choice. But transitive pinning applies the pin to `Microsoft.EntityFrameworkCore.Design`
  10.0.12 too, which needs `Microsoft.CodeAnalysis.CSharp` 5.0.0 or later, so 4.14.0 fails restore with
  NU1109 in Persistence. 5.0.0 is the floor. The ceiling is the compiler of the SDK in `global.json`.
  SDK 10.0.401 ships compiler **5.9.0-1.26423.113** (its `Roslyn/bincore/csc.dll -version`; SDK
  10.0.302's prints 5.6.0-2.26329.109). Dependabot proposes only patch updates of
  `Microsoft.CodeAnalysis.*`, and the SDK-bump checklist in `docs/development.md` says how far the
  pin may move.
- **Coverage**: the analyzer is build-time tooling, never deployed. `eng/coverage.runsettings`
  leaves it out and `coverage-summary.py` never counts it, so no ring floor depends on it. Its
  facts in `ResultDiscardAnalyzerTests` are its specification.

## Consequences

- A dropped result is a red Release build in every project, tests included, and a warning in the
  Debug loop. A deliberate drop reads `_ = …` with a one-line reason; a test's arrange step ends in
  `ShouldBeSuccess()`.
- The generated project owns about 150 lines of analyzer and a test class, and one coupling: the Roslyn pin
  moves with the SDK, by hand, never above its compiler, and never below the floor EF Core Design
  sets. A microsoft-group Dependabot PR that fails restore with NU1109 on `Microsoft.CodeAnalysis.CSharp`
  raises the pin in the same PR.
- Gaps, each named so nobody assumes otherwise. An unawaited `Task<Result>` is left to CS4014, which
  reports it only inside an async method. The NSubstitute exemption is by method name and namespace,
  and a call inside `Received.InOrder(...)` or `When(...)` is not exempt (write `_ =` there). A
  `Result` stored in a variable and never read is not RSLT001's business.

## Alternatives / Revisit when

- **CA1806 with `additional_use_results_methods`** (the stand-in, retired by this ADR): by name, never
  through `await`, and blind to every method outside the listed prefixes (handlers, ports, Persistence,
  `IdentityResult`).
- **The source project's own analyzer package**: it matches only its own generic result type in its own
  namespace, so it would be inert here and would miss the non-generic `Result`.
- **JetBrains `[MustUseReturnValue]`**: an IDE hint, a package, and no failed build.
- Revisit when the C# compiler itself reports a dropped value of a type marked must-use: the analyzer
  then becomes an attribute on `Result` and `Result<T>`.
