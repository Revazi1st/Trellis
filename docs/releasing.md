# Releasing the template

How a version of the `Trellis.Templates` package (the `dotnet new trellis` template) reaches
nuget.org. Everything runs in [`.github/workflows/release.yml`](../.github/workflows/release.yml);
this page is what a maintainer does around it. Generated projects do not ship that workflow.

## What the workflow does

```
prepare ──► test ─────────┐
        └─► template ─────┴─► package ──────────────────────► publish (environment: nuget.org)
            (matrix)          pack, smoke test, attest,        download the drafted package,
                              draft the GitHub release         NuGet login, push, publish release
```

| Job | Steps | Dry run |
|---|---|---|
| `prepare` | Resolve the version (from the tag, or on a branch from the `version` input or `<Version>` in `eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj`); fail unless the commit is on `main`; fail unless `CHANGELOG.md` has a `## [X.Y.Z]` section | runs; "not on main" is a warning |
| `test` | `dotnet build -c Release` (warnings are errors) and every test, Aspire E2E included | runs |
| `template` | the whole `template-ci.yml` matrix: every toggle leg instantiated, built and tested | runs |
| `package` | pack with `-p:Version=X.Y.Z`, install the `.nupkg` and build an instance of it, attest its build provenance, create a **draft** GitHub release with the CHANGELOG section as notes and the `.nupkg` attached | runs; the draft stays |
| `publish` | in the `nuget.org` environment: download the `.nupkg` from the draft, log in to nuget.org by trusted publishing, push, publish the release | runs up to the login; **push and publish are skipped** |

Nothing is public until `publish`: the package is attested and drafted first, the push happens
next, and the GitHub release is published only after the push succeeded.

## One-time setup

1. **nuget.org account.** Sign in with the account (or organization) that owns, or will own, the
   `Trellis.Templates` package id.
2. **Trusted publisher on nuget.org** (no API key is stored anywhere). Under your account's
   *Trusted Publishing* page, add a GitHub Actions policy: repository owner `Revazi1st`,
   repository `trellis`, workflow file `release.yml`, environment `nuget.org`.
3. **`NUGET_USER` repository variable** — the nuget.org **profile name** (not the e-mail) that
   owns the policy: *Settings → Secrets and variables → Actions → Variables*. `NuGet/login`
   exchanges the workflow's OIDC token for a short-lived API key on behalf of that user.
4. **`nuget.org` environment** — *Settings → Environments → New environment*, named exactly
   `nuget.org`. Add yourself (or the release owners) as required reviewers, and limit deployments
   to the `main` branch and `v*` tags (dry runs run on `main`, releases on tags).
5. **A public repository.** Build-provenance attestations are available to public repositories
   on every plan; a private repository needs GitHub Enterprise Cloud, otherwise the `Attest build
   provenance` step fails, and the release with it.
6. **The `docs-not-needed` label.** The CI `Docs drift` job (`scripts/check-docs-drift.sh`)
   skips itself on a pull request carrying it — the escape hatch for refactors that change no
   behaviour. Create it once:
   `gh label create docs-not-needed --description "No feature or threat-model doc change needed"`.

## Cutting a release

1. **Pick the version.** SemVer: a breaking change to generated output or to a toggle's meaning
   is a major bump (a minor bump while below 1.0), a new toggle or feature a minor bump, a fix a
   patch. A pre-release (`X.Y.Z-rc.1`) is published as a GitHub pre-release.
2. **Write the release notes.** In a pull request to `main`, move the entries under
   `## [Unreleased]` in `CHANGELOG.md` into a new `## [X.Y.Z] - YYYY-MM-DD` section
   ([Keep a Changelog](https://keepachangelog.com/en/1.1.0/)), written for template consumers.
   The section becomes the GitHub release notes verbatim. Merge once CI is green.
3. **Dry run.** *Actions → Release → Run workflow* on `main`, `dry_run` ticked, `version` X.Y.Z —
   or `gh workflow run release.yml --ref main -f dry_run=true -f version=X.Y.Z`. Approve the
   `nuget.org` environment when it asks. Then check the run is green, the draft release's notes
   and attached `.nupkg`, and the provenance:
   `gh attestation verify Trellis.Templates.X.Y.Z.nupkg --repo Revazi1st/trellis` (download the
   asset from the draft first).
4. **Tag and push.** Tag the `main` commit the dry run used, annotated:
   `git tag -a vX.Y.Z -m "vX.Y.Z" <sha>` then `git push origin vX.Y.Z`. The push starts the real
   run; approve the `nuget.org` environment when `publish` reaches it. The run replaces the
   dry run's draft with its own.
5. **Verify.** The GitHub release is published; the package page
   (<https://www.nuget.org/packages/Trellis.Templates>) shows X.Y.Z once nuget.org has indexed it
   (minutes); and it installs:
   `dotnet new install Trellis.Templates::X.Y.Z --debug:custom-hive "$(mktemp -d)"`.

## When something fails

- **Before `publish`**: nothing is public. Fix the cause on `main`; if the fix changes the tagged
  commit, delete the tag (`git push --delete origin vX.Y.Z`, `git tag -d vX.Y.Z`) and tag the new
  commit. Re-running replaces a leftover draft for the tag; a published release is never touched.
- **CHANGELOG gate**: the tag points at a commit without the `## [X.Y.Z]` section — merge the
  notes, then re-tag as above.
- **NuGet login**: `NUGET_USER` is unset or not the policy owner's profile name, or the policy's
  repository, workflow file or environment does not match this run exactly.
- **Push succeeded, publish failed**: re-run the `publish` job (the push uses `--skip-duplicate`)
  or publish by hand with `gh release edit vX.Y.Z --draft=false`.
- **A bad version reached nuget.org**: versions cannot be deleted or replaced. Unlist it on
  nuget.org and release a patch.
