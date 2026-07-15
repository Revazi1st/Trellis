# Contributing to XTemplate

Thanks for helping improve XTemplate. This is both a working solution and a
`dotnet new` template, so changes should keep the reference solution green and
the template instantiable.

## Getting started

No local .NET SDK is required — everything runs through the SDK container:

```bash
docker build -t xtemplate-sdk:10.0 scripts/sdk-image   # one-time
./scripts/dotnet.sh build XTemplate.slnx
./scripts/dotnet.sh test  XTemplate.slnx
./scripts/dotnet.sh format XTemplate.slnx
```

With a local .NET 10 SDK you can also `aspire run` the full app. See
[docs/development.md](docs/development.md).

## Before opening a PR

- `dotnet build XTemplate.slnx -warnaserror` and `dotnet test` are green.
- `dotnet format --verify-no-changes` is clean.
- New/changed features update their doc under `docs/features/` (the CI docs-drift
  check enforces this) and, if you made a load-bearing decision, add an ADR
  (`docs/adr/`, see the `new-adr` skill).
- Follow the layer conventions in `.claude/rules/` and the boundaries in
  `AGENTS.md`. In particular: dependencies point inward, failures are `Result`s
  (not exceptions), migrations are generated (never hand-edited), and **no
  commercially-licensed packages** (see the ceilings in `Directory.Packages.props`).

## Commit style

Conventional commits (`feat:`, `fix:`, `docs:`, `chore:`, `test:`). Keep each
commit green and self-contained.

## Reporting security issues

Do not open a public issue for vulnerabilities — see [SECURITY.md](SECURITY.md).
