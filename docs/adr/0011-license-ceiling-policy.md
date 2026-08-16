# ADR-0011: License-ceiling policy for encumbered packages

- **Status**: accepted
- **Date**: 2026-07-14

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

The whole shipped stack is free-for-commercial. `AGENTS.md` and the `Ask first: adding a NuGet package` boundary route package additions through this file. The ceiling is enforced, not advisory: the `EnforceLicenseCeilings` target in `Directory.Build.props` fails the build on any forbidden `PackageReference`, and Dependabot's ignore list blocks the bot-upgrade path.

## Consequences

- Consumers inherit no commercial license obligation.
- The ceiling comment is the single source of truth agents consult before adding packages.
- Re-verify ceilings periodically; licensing terms and thresholds change.
