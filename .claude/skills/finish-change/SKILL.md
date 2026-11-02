---
name: finish-change
description: Run the repository's gates on a finished change in CI's order (format, Release build, tests, the OpenAPI snapshot, docs drift, links, the CHANGELOG line, the reviewers), then commit or open the pull request only when asked. Use when a change is done and about to be committed, pushed or opened as a pull request.
argument-hint: "[commit|pr]"
allowed-tools: Bash(./scripts/dotnet.sh:*), Bash(UPDATE_OPENAPI_SNAPSHOT=1 ./scripts/dotnet.sh:*), Bash(./scripts/check-docs-drift.sh:*), Bash(./scripts/check-adr-links.sh:*), Bash(./scripts/check-adr-history.sh:*), Bash(git status), Bash(git diff:*), Bash(git log:*)
---

# Finish a change

The gates CI runs, run here first and in its order, so a change fails on your machine rather than in
review. Every box says what fails when it is skipped, in brackets when a machine fails it: a job of
`.github/workflows/ci.yml`, a test or a script. A fix made for a later box means the earlier ones run
again. The argument (here: `$ARGUMENTS`) says how far to go: none stops after the reviewers with a
proposed commit message, `commit` also commits, and `pr` commits and opens the pull request.

## Gates

- [ ] **Scope the diff.** `git status`, `git diff` and `git diff --staged`: name the files this change
      touches, untracked ones included, and set anything unrelated aside before going on. Every box
      below keys on this list, and a stray file rides into the commit and into every reviewer's reading
      of the change.
- [ ] **Format, then verify.** `./scripts/dotnet.sh format Trellis.slnx`, then
      `./scripts/dotnet.sh format Trellis.slnx --verify-no-changes --severity warn`, which must report
      nothing [CI *Build & Test*, step *Format check*].
- [ ] **Release build.** `./scripts/dotnet.sh build Trellis.slnx -c Release`: Release turns every
      warning into an error, RSLT001 (a discarded `Result`) and NuGetAudit's advisories included
      [CI *Build & Test*, *Build (warnings as errors)*]. RSLT001 or CS9057: `docs/development.md`,
      *When the build fails*; an advisory or a package: `.claude/rules/dependencies.md`.
- [ ] **Tests, narrowed, then whole.** First the project you touched, narrowed to what changed
      (`./scripts/dotnet.sh test tests/Trellis.Application.UnitTests --filter-class '*CommandHandlerTests'`,
      or `--filter-method`), then everything: `./scripts/dotnet.sh test Trellis.slnx`. Never filter the
      `.slnx`: every other project then runs zero tests and exits 8, red although yours passed
      (`docs/development.md`, *Running part of the suite*), and a run that reports zero tests is a
      failure to look at, never a pass. A bug fix's test was seen failing without the fix, for the
      expected reason (`AGENTS.md`). IntegrationTests and ApiTests need Docker [CI *Build & Test*,
      *Test (with coverage)*; the Stop hook reminds once when a `.cs` or `.razor` file under `src/` or
      `tools/` changed and no test ran].
- [ ] **The OpenAPI snapshot**, when anything under `src/Presentation/Api/` changed (Api, Api.Common,
      Api.Contracts): `UPDATE_OPENAPI_SNAPSHOT=1 ./scripts/dotnet.sh test tests/Trellis.ApiTests`, then
      `git diff docs/api/openapi.v1.json`, read to the end: every operation, schema and status that moved
      is one the change meant to move, and anything else is a contract change nobody asked for
      [`OpenApiSnapshotTests`; CI *API contract* diffs the running app's document against the file].
- [ ] **Docs drift and links.** `./scripts/check-docs-drift.sh --working-tree`: the feature doc,
      `docs/threat-model.md` or `docs/design-system.md` the change owes (once committed, run it without
      the flag, which compares the branch with `origin/main` as CI does) [CI *Docs drift*; the edit hook
      runs the same check]. Then `./scripts/check-adr-links.sh`: every ADR, rule, skill, reviewer, hook
      and feature doc listed in `docs/INDEX.md`, every rule glob and relative link resolving [CI
      *Build & Test*, *ADR link check*]. A change to an existing ADR also runs
      `./scripts/check-adr-history.sh`: an accepted ADR only gains a dated amendment [CI *ADR history*].
      <!--#if (TemplateSource) -->
      In the template's source, also `./scripts/check-template-toggles.sh`: every directive alone on its
      line and every symbol declared [CI *Build & Test*, *Template toggle check*]; and
      `python3 eng/readme/render-diagrams.py --check`: the README's pictures match the tree, re-rendered
      without `--check` [CI *Build & Test*, *README pictures are current*].
      <!--#endif -->
- [ ] **The CHANGELOG line**, when someone using the project would notice the change (behaviour, a
      contract, configuration, a fixed bug): one entry under `## [Unreleased]` in `CHANGELOG.md`, in
      their words; none for a refactor. Nothing fails without it, which is why it is a box: the next
      release notes would leave the change out.
- [ ] **The reviewers the change needs**, as the `AGENTS.md` routing (*Boundaries*) names them:
      `code-reviewer` for any change under `src/`; `architecture-guard` when it adds a project, a
      reference or a package, or crosses a layer; `test-reviewer` when it adds or changes a test or fixes
      a bug; `security-reviewer` when it touches authentication, identity, hardening, the Auth feature or
      who may call an endpoint.
      <!--#if (IncludeUI) -->
      `ui-reviewer` for a change under `src/Presentation/UI/` (a page, a layout, a form, a stylesheet).
      <!--#endif -->
      No machine stands behind this box: the review is the gate. A finding is a claim: check it against
      the code or a test before acting on it, push back when it would undo a decision an ADR records, and
      run the gates above again after a fix.

Without `commit` or `pr`, stop here: list each box with the command that ran and its result (or why it
did not apply) and propose the commit message. Nothing is committed or pushed unasked.

## Commit (`commit` or `pr`)

- [ ] On `main`, branch first: `git switch -c <type>/<short-name>`.
- [ ] One commit per concern, each green on its own: a refactor and the feature it makes room for are
      two commits, in that order, and the gates pass at each. Stage files by name from the first box's
      list, never `git add -A` over a tree that holds anything else.
- [ ] The subject in the style of `AGENTS.md`'s *Commits* line: `type(scope): subject` in the
      imperative, the type one of `feat fix docs test refactor perf build ci chore`; the body says why.
- [ ] Never a secret: no `.env`, key, token or connection string with a password in
      `git diff --staged`, read once more before committing [CI *Secret scan* runs gitleaks over the
      whole history, where a pushed secret stays, compromised, until it is rotated; the `git commit`
      hook asks when the subject does not fit and, with gitleaks installed, scans the staged diff].

## Pull request (`pr`)

- [ ] Push the branch (`git push -u origin HEAD`) and open the pull request:
      `gh pr create --title "<the commit subject>" --body-file <file>`, the body
      `.github/PULL_REQUEST_TEMPLATE.md` filled in. *What & why* in a few sentences, linking the ADR or
      issue; a checklist box ticked only when its command ran green in this session, the others left
      unticked with a word on why (a box ticked on faith is worse than an empty one) [CI runs every job
      above on the pull request; the docs-drift job honours a `docs-not-needed` label for a change that
      alters no behaviour].
- [ ] Report the pull request's URL and the checks still running (`gh pr checks`).
