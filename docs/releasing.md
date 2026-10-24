# Releasing the template

How a version of the `Trellis.Templates` package (the `dotnet new trellis` template) reaches
nuget.org, and its documentation reaches the website. Everything runs in
[`.github/workflows/release.yml`](../.github/workflows/release.yml), which deploys the site through
[`.github/workflows/site.yml`](../.github/workflows/site.yml); this page is what a maintainer does
around them. Generated projects ship neither workflow.

## What the workflow does

```
prepare ──► test ─────────┐
        └─► template ─────┴─► package ──────────────────────► publish (environment: nuget.org) ──► site
            (matrix)          pack, smoke test, attest,        download the drafted package,       site.yml from the tag:
                              draft the GitHub release         NuGet login, push, publish release  build, deploy to Pages
```

| Job | Steps | Dry run |
|---|---|---|
| `prepare` | Resolve the version (from the tag, or on a branch from the `version` input or `<Version>` in `eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj`); fail unless the commit is on `main`; fail unless `CHANGELOG.md` has a `## [X.Y.Z]` section | runs; "not on main" is a warning |
| `test` | `dotnet build -c Release` (warnings are errors) and every test, Aspire E2E included | runs |
| `template` | the whole `template-ci.yml` matrix: every toggle leg instantiated and built in Release, and the legs flagged `test: true` tested, the Aspire E2E that boots the generated AppHost included (the browser tests stay opt-in); the default leg again on Windows; a project built from the packed template | runs |
| `package` | pack with `-p:Version=X.Y.Z`, install the `.nupkg` and build an instance of it, attest its build provenance, create a **draft** GitHub release with the CHANGELOG section as notes and the `.nupkg` attached | runs; the draft stays |
| `publish` | in the `nuget.org` environment: download the `.nupkg` from the draft, log in to nuget.org by trusted publishing, push, publish the release | runs up to the login; **push and publish are skipped** |
| `site` | `site.yml` with the tag: build the documentation site from the tagged commit (every link checked) and deploy it to GitHub Pages in the `github-pages` environment; every page's footer names the version | **skipped** |

Nothing is public until `publish`: the package is attested and drafted first, the push happens
next, and the GitHub release is published only after the push succeeded. The site follows the
published release, so it never documents a version nuget.org does not have.

## One-time setup

1. **nuget.org account.** Sign in with the account (or organization) that owns, or will own, the
   `Trellis.Templates` package id.
2. **Trusted publisher on nuget.org** (no API key is stored anywhere). Under your account's
   *Trusted Publishing* page, add a GitHub Actions policy: repository owner `Revazi1st`,
   repository `Trellis`, workflow file `release.yml`, environment `nuget.org`.
3. **`NUGET_USER` repository variable** — the nuget.org **profile name** (not the e-mail) that
   owns the policy: *Settings → Secrets and variables → Actions → Variables*. `NuGet/login`
   exchanges the workflow's OIDC token for a short-lived API key on behalf of that user.
4. **`nuget.org` environment** — *Settings → Environments → New environment*, named exactly
   `nuget.org`. Add yourself (or the release owners) as required reviewers, and limit deployments
   to the `main` branch and `v*` tags (dry runs run on `main`, releases on tags).
5. **A public repository (for attestation).** Build-provenance attestations are available to
   public repositories on every plan; a private repository needs GitHub Enterprise Cloud. On any
   other private repository the `Attest build provenance` step is skipped with a warning and the
   release goes out unattested, so `gh attestation verify` finds nothing for that package.
6. **The `docs-not-needed` label.** The CI `Docs drift` job (`scripts/check-docs-drift.sh`)
   skips itself on a pull request carrying it — the escape hatch for refactors that change no
   behaviour. Create it once:
   `gh label create docs-not-needed --description "No feature or threat-model doc change needed"`.
7. **Dependency bumps merge on their own.** `.github/workflows/dependabot-auto-merge.yml` enables
   GitHub's auto-merge on Dependabot's patch and minor pull requests for NuGet packages, images and
   dev-container features. Three kinds wait for a person: a major, or a group that contains one; a
   .NET SDK bump (Dependabot calls a feature-band jump a patch, yet it raises the SDK everyone
   needs); and the weekly GitHub Actions group, whose pull request edits workflow files, which the
   Actions token may never merge. `dependabot.yml` holds every new version back for a week
   (`cooldown`; version updates only, a security update still arrives at once). Two repository
   settings make it work, both set once: *Settings → General → Allow auto-merge* (with
   *Automatically delete head branches*), and a ruleset on `main` that requires the CI, Template CI
   and CodeQL jobs, requires a pull request, gates on CodeQL alerts and forbids force-pushes and
   deletion (set `require_extra_approval_for_unattributed_changes` explicitly: the API fills it in
   as `true` when omitted, and with no reviewers that blocks any commit whose author e-mail is not
   linked to a GitHub account). The workflow checks for both before it acts and only leaves a notice without them,
   because `gh pr merge --auto` on an unprotected branch would merge at once. Two consequences to
   know: a merge made by the Actions token starts no push workflow on `main`, and each weekly pull
   request was tested against the `main` of its own moment, so `ci.yml` (template source only) and
   `template-ci.yml` also run once a night on `main` to test the combined tree.
8. **GitHub Pages, for the documentation site** (<https://revazi1st.github.io/Trellis/>). Two settings
   only the repository's owner can make:
   - *Settings → Pages → Build and deployment → Source*: **GitHub Actions**. This also creates the
     `github-pages` environment.
   - *Settings → Environments → `github-pages` → Deployment branches and tags*: keep `main` and **add a
     tag rule, `v*`**. A release deploys from its tag, and the environment GitHub creates allows only
     the default branch, so without the rule the release's `site` job fails with
     `Tag "vX.Y.Z" is not allowed to deploy to github-pages due to environment protection rules.`
     (a preview dispatched on another branch fails the same way:
     `Branch "<name>" is not allowed to deploy to github-pages due to environment protection rules.`).
     A preview started on `main` is allowed already, whatever ref it builds.

   Then require the `Build and validate` job of `site.yml` on `main`'s ruleset beside the CI jobs: it
   runs on every pull request and builds the site only when the pull request touches what the site
   renders, passing (as skipped) otherwise.

## Before a minor release: an agent dry run

The template's guidance (`AGENTS.md`, the rules, the skills) is code for agents, and it drifts like
code. Before a minor version, pack the release candidate, generate a fresh project from it into a
private hive (`dotnet new install <nupkg> --debug:custom-hive <dir>`), and give an agent one fixed
feature spec (keep the same one from release to release, for example a `Customer` with a unique e-mail
that can be created, renamed and deactivated, which members may list) and nothing but the generated
repository. Judge the result by the gates that already exist: the Release build, the whole test suite
with the architecture tests, `scripts/check-docs-drift.sh` and the OpenAPI snapshot. Then read the diff
for what no gate sees: the rule in the aggregate rather than the handler, the feature doc written first,
the shapes from the `add-feature` skill's "not shown" list handled the way it points. Each miss becomes
a fix to a rule, a skill box or a test in the release, never a note to the agent. One run is anecdote,
and results vary between models and attempts: it is a smoke test of the guidance, not a score.

## Cutting a release

1. **Pick the version.** First read the dependency bumps that merged on their own since the last
   tag (`git log vPREV..main --oneline --author=dependabot`); they are part of what ships. SemVer: a breaking change to generated output or to a toggle's meaning
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
   `gh attestation verify Trellis.Templates.X.Y.Z.nupkg --repo Revazi1st/Trellis` (download the
   asset from the draft first).
4. **Tag and push.** Tag the `main` commit the dry run used, annotated:
   `git tag -a vX.Y.Z -m "vX.Y.Z" <sha>` then `git push origin vX.Y.Z`. The push starts the real
   run; approve the `nuget.org` environment when `publish` reaches it. The run replaces the
   dry run's draft with its own.
5. **Verify.** The GitHub release is published; the package page
   (<https://www.nuget.org/packages/Trellis.Templates>) shows X.Y.Z once nuget.org has indexed it
   (minutes), with a Release Notes link to that GitHub release (`PackageReleaseNotes` in the pack
   project, built from the version); it installs:
   `dotnet new install Trellis.Templates::X.Y.Z --debug:custom-hive "$(mktemp -d)"`; and
   <https://revazi1st.github.io/Trellis/> has no preview banner and names `Trellis.Templates X.Y.Z` in
   every page's footer.

## The documentation site

The site is `site/` (see its README): Astro and Starlight rendering `docs/` in place. GitHub Pages
holds one copy of it, and three things replace that copy:

- **A release**: the `site` job above, from the tag.
- **A preview**: `gh workflow run site.yml -f ref=main` (any branch, tag or SHA; the default is
  `main`). It builds that ref and deploys it in place of the released site, with a banner on every
  page saying it is a preview of that ref and commit and not the released documentation (before the
  first release: that `Trellis.Templates` is not on nuget.org yet). Put the release back afterwards
  with `gh workflow run site.yml -f ref=vX.Y.Z`, which rebuilds it from its tag, no banner.
- **A fix to the site itself after a release** (a page that renders wrong, a broken style): merge it
  to `main`, then `gh workflow run site.yml -f ref=vX.Y.Z -f site_ref=main`. The docs still come
  from the tag and `site/` from `main`, so the release's documentation stays the release's. A fix to
  `docs/` is documentation of the next version and ships with it.

Pull requests that touch `docs/`, `site/`, `README.md`, `.template.config/`, `global.json`,
`eng/readme/` or a source file a site page quotes run the same build without deploying it: a link or
an `#anchor` that points nowhere fails it.

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
- **The `site` job failed** with `Tag "vX.Y.Z" is not allowed to deploy to github-pages`: add the
  `v*` tag rule (one-time setup, step 8), then re-run the failed job, or run
  `gh workflow run site.yml -f ref=vX.Y.Z`. The package and the release are already public; only the
  site waits. Any other failure of the build is a link or a page to fix: fix it on `main` and redeploy
  with `-f site_ref=main` as above.
