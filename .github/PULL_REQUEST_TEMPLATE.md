## What & why

<!-- What does this change and why. Link issues/ADRs. -->

## Checklist

- [ ] `dotnet build Trellis.slnx -c Release` and `dotnet test Trellis.slnx` are green (Release turns warnings into errors, like CI)
- [ ] `dotnet format --verify-no-changes --severity warn` is clean
- [ ] `docs/api/openapi.v1.json` regenerated after an endpoint or contract change (`UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/Trellis.ApiTests`)
- [ ] Feature docs updated under `docs/features/` (if behavior changed)
- [ ] A line under `## [Unreleased]` in `CHANGELOG.md` (if a consumer would notice the change)
- [ ] An ADR added under `docs/adr/` (if a load-bearing decision was made)
- [ ] No commercially-licensed packages added (see `Directory.Packages.props`)
- [ ] Anything taken from another project is credited, and any notice its license requires comes with it
