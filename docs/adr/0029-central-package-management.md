# ADR-0029: Central package management, transitive pinning, grouped Dependabot updates

- **Status**: accepted
- **Date**: 2026-10-08
- **Amended**: 2026-10-11 — Dependabot's patch and minor pull requests merge on their own behind a week of cooldown and the required checks (see *Amendment (2026-10-11)* below)

## Context

A solution of nearly twenty projects that reference overlapping packages drifts when each project states
its own versions: two projects resolve different versions of one library, an upgrade is a hunt
through csproj files, and a vulnerable transitive package hides behind the package that pulls it in.
Bots make it worse when they move one package of a family that only works in lock-step: the
Microsoft.Extensions, ASP.NET Core, EF Core and Aspire packages share version floors, and the
xunit.v3 and Microsoft.Testing.Platform packages share a runtime contract.

## Decision

- **Central package management.** `Directory.Packages.props` holds every version
  (`ManagePackageVersionsCentrally`); csproj files carry `PackageReference` without versions. Analyzers
  every project needs are `GlobalPackageReference`s there (SonarAnalyzer.CSharp, Meziantou.Analyzer,
  build-time only).
- **Transitive pinning on** (`CentralPackageTransitivePinningEnabled`). A `PackageVersion` also pins
  the package where it arrives transitively, which is how a vulnerable transitive is patched without
  waiting for its parent (the `Microsoft.OpenApi` entry is the worked example). The price: a pin
  below a transitive requirement is a hard NU1109 downgrade error, so families move together.
- **The license ceilings live in the same file** (ADR-0011), and the build enforces them over the
  resolved graph.
- **NuGetAudit audits the whole graph** (`NuGetAuditMode` `all`, `NuGetAuditLevel` `low`), and Release
  and CI builds treat its warnings as errors; `docs/development.md` ("Vulnerable packages") is the
  playbook: upgrade, else pin the transitive with the advisory in a comment, else suppress one advisory
  with its reason.
- **Dependabot groups what must move together** (`.github/dependabot.yml`): one `microsoft` group for
  ASP.NET Core, EF Core (with the `dotnet-ef` local tool), Microsoft.Extensions, IdentityModel,
  OpenApi, Aspire and Npgsql; one for OpenTelemetry; one `test` group for xunit, Microsoft.Testing,
  Playwright and the test libraries; all GitHub Actions in one weekly PR (SHA pins with version
  comments); the SDK in `global.json` without major versions; the SDK image, the compose images and
  the dev-container features. The ceiling packages are on its ignore list.

## Consequences

- One file answers "which version of X do we run", and an upgrade is one line plus whatever the
  group drags along.
- A Dependabot PR is a coherent change that builds, rather than a fragment that fails restore; the
  groups are coarse on purpose and their PRs deserve a real review.
- A transitive pin is a debt with a reason: delete it once the parent depends on a fixed version.
- A new package family that shares floors with an existing group joins that group in
  `dependabot.yml` in the same change that adds it (adding a package is an ask-first change,
  `AGENTS.md`).

## Amendment (2026-10-11): bumps that merge on their own

Routine bumps no longer wait for a person. `.github/workflows/dependabot-auto-merge.yml` enables
GitHub's auto-merge on Dependabot's patch and minor pull requests once every required check on
`main` is green; `dependabot.yml` holds each new version back for a week (`cooldown`) so a yanked or
compromised release is usually gone before a pull request exists. A major, or a group containing
one, a .NET SDK bump and the GitHub Actions group still wait for review. The workflow acts only
when the repository allows auto-merge and a ruleset requires status checks on the base branch; the
one-time setup and the two consequences (no push run for a token-made merge, hence the nightly CI
and Template CI runs on `main`; each weekly pull request tested against the `main` of its moment) are in
`docs/releasing.md`. The licence check of ADR-0011 covers the listed package ids only, so the
release runbook now starts with a read of the bumps that merged since the last tag.
