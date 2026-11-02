---
description: Adding or moving a package, the license ceilings, central versions and Dependabot groups, a NuGetAudit advisory that turns the build red, and bumping the SDK
paths: ["Directory.Packages.props", "Directory.Build.props", "global.json", ".github/dependabot.yml", "scripts/sdk-image/**", ".config/dotnet-tools.json"]
---

# Dependency rules

## Adding or moving a package

- **Ask first** before a new package (`AGENTS.md`), and read the LICENSE CEILINGS block in `Directory.Packages.props` (ADR-0011): `MediatR`, `AutoMapper`, `FluentAssertions`, `MassTransit` and `Moq` never enter. Two targets in `Directory.Build.props` refuse them over one list (`LicenseCeilingPackageIds`): `EnforceLicenseCeilings` a direct `PackageReference`, before restore, and `EnforceLicenseCeilingsOnResolvedGraph` any package the restore resolved, transitive included. Dependabot's ignore list never proposes them.
- **Central versions** (ADR-0029): a version lives only in `Directory.Packages.props`, as a `PackageVersion` (a `GlobalPackageReference` for an analyzer every project gets), never on a project's `PackageReference`. Transitive pinning is on, so a pin below what another package requires fails restore with NU1109.
- **A family that shares version floors moves in one Dependabot group** in `.github/dependabot.yml` (`microsoft`, `opentelemetry`, `roslyn`, `test`): add a new member to its group in the same change. A package numbered after .NET (10.x on .NET 10) also gets a semver-major ignore there unless a pattern already covers it: its next major comes with the next .NET.

## A red build from NuGetAudit

`NuGetAudit` (`NuGetAuditMode` `all`, `NuGetAuditLevel` `low`, `Directory.Build.props`) turns an advisory anywhere in the graph, transitive included, into NU1901–NU1904, an error in Release and CI. Answer it in this order (`docs/development.md`, *Vulnerable packages*):

1. Find the path: `dotnet nuget why Trellis.slnx <package>` shows which reference pulls it in.
2. Upgrade the direct reference when a newer version of it, or of the package that depends on the vulnerable one, brings the fix.
3. Otherwise pin the transitive package: a `PackageVersion` at the patched version in `Directory.Packages.props` with a comment naming the advisory (the `Microsoft.OpenApi` entry is the model); delete the pin once the parent depends on a fixed version itself.
4. Only when no fixed version exists, or the vulnerable code provably cannot be reached: one `<NuGetAuditSuppress Include="https://github.com/advisories/GHSA-…" />` in `Directory.Build.props`, with why it does not apply and when to look again. One advisory per entry, never a whole package.
5. Never lower `NuGetAuditLevel` or `NuGetAuditMode` to get a build through; raising the level is a decision for an ADR. NU1900 is not an advisory: the advisory data could not be downloaded, so retry once the feed is reachable.

## Bumping the SDK

- `global.json` pins the SDK with `rollForward: latestFeature`, and CI installs exactly that version (`.github/actions/setup-dotnet` reads it). The SDK image (`scripts/sdk-image/Dockerfile`, pinned by digest) must carry an SDK `global.json` accepts, or `./scripts/dotnet.sh` cannot build.
- `Microsoft.CodeAnalysis.CSharp` may rise to the new compiler's version, never above it (CS9057, ADR-0030), and stays at or above the Roslyn floor of `Microsoft.EntityFrameworkCore.Design` (NU1109).
- `dotnet-ef` in `.config/dotnet-tools.json` stays at the EF Core runtime's version; Dependabot's `microsoft` group moves the two together.
- A new .NET major is a migration, not a bump: the target framework, the SDK image's tag and every package numbered after .NET move by hand, in one change (`docs/development.md`, *Bumping the SDK*).
