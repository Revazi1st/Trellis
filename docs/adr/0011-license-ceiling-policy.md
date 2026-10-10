# ADR-0011: License-ceiling policy for encumbered packages

- **Status**: accepted
- **Date**: 2026-07-14
- **Amended**: 2026-10-08 — scope: what ships, and what build-time analyzers and the template's own notice are (see *Amendment* below)

## Context

The 2025–26 .NET OSS wave moved several defaults to commercial licenses. A redistributable `dotnet new` template must not bake in a package that forces every consumer through a paid/license-key flow — even the "free tier" ones. This must be enforceable against both humans and AI agents (which "helpfully" upgrade packages).

## Decision

Maintain a **LICENSE CEILINGS** block at the top of `Directory.Packages.props` naming the packages never to add or upgrade past their last-free version:

| Package | Ceiling | Reason | This template instead |
|---|---|---|---|
| MediatR | ≥ 13.0.0 | Lucky Penny, RPL-1.5/commercial | hand-rolled CQRS (ADR-0003) |
| AutoMapper | ≥ 15.0.0 | Lucky Penny, RPL-1.5/commercial | manual mapping |
| FluentAssertions | ≥ 8.0.0 | Xceed, proprietary | Shouldly |
| MassTransit | ≥ 9.0.0 | Massient, commercial | Wolverine/Rebus if messaging needed |
| Moq | any version | MIT, but 4.20.0 shipped SponsorLink, which read the developer's git e-mail at build time and sent a hash of it off the machine — a trust break, not a license one | NSubstitute |

The whole shipped stack is free-for-commercial. `AGENTS.md` and the `Ask first: adding a NuGet package` boundary route package additions through this file. The ceiling is enforced, not advisory: `Directory.Build.props` fails the build on a forbidden direct `PackageReference` (`EnforceLicenseCeilings`, before restore) and on a forbidden id anywhere in a project's resolved package graph, transitive dependencies included (`EnforceLicenseCeilingsOnResolvedGraph`, after `ResolvePackageAssets`); both read one list, `LicenseCeilingPackageIds`. Dependabot's ignore list blocks the bot-upgrade path.

## Consequences

- Consumers inherit no commercial license obligation.
- The ceiling comment is the single source of truth agents consult before adding packages.
- Re-verify ceilings periodically; licensing terms and thresholds change.

## Amendment (2026-10-08): what the policy covers

The ceilings govern what reaches a consumer: a package that ships in a generated product's output, or
one that forces a license flow on everyone who builds it. Two things sit outside them and are stated
here so nobody has to guess:

- **Build-time analyzers** (`GlobalPackageReference` in `Directory.Packages.props`: SonarAnalyzer.CSharp,
  Meziantou.Analyzer) run inside the compiler and ship in no output; their terms bind the build only.
  A new one is still an ask-first package addition (`AGENTS.md`), and its license is read before it is
  added like any other's.
- **The template's own notice.** A generated project keeps the template's MIT `LICENSE` (copied
  verbatim, never renamed with the project), because the MIT terms require it to travel with
  substantial portions of the template. It does not license the product: the generated README asks
  the project to add its own terms.
