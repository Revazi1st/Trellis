# Changelog

All notable changes to the Trellis template are documented here, written for the people who
generate projects from it. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). A generated project gets its own empty
changelog; its `.trellis-template.json` records which version of this one it came from.

## [Unreleased]

- **E-mail goes out through MailKit, and `aspire run` catches it in Mailpit.** The SMTP adapter
  (`Email:Provider=Smtp`) submits with MailKit (MIT) in place of `System.Net.Mail.SmtpClient`, which
  Microsoft no longer recommends for new code; the `Email:Smtp` settings, their boot checks and the
  logging contract are unchanged, STARTTLS still fails a send rather than falling back to plaintext,
  and a failure is still thrown for the outbox with the relay's reply left out, now as MailKit's
  `SmtpCommandException` (status and error code kept) where it was `SmtpException`. In run mode only,
  the AppHost starts Mailpit (`CommunityToolkit.Aspire.Hosting.MailPit`, image `axllent/mailpit`,
  both MIT) and points the API at it, so the receipt of a paid invoice lands behind the `mailpit`
  resource's **Inbox** link instead of in the log; `aspire publish` declares no catcher, so a
  deployment still configures its own relay. Under `aspire run` the AppHost's settings outrank a relay
  you put in user secrets: run the API from its launch profile to try a real one.
- **The `--use-redis` cache is Valkey** (BSD-3-Clause) in place of the Redis server, whose 8.x
  releases are RSALv2, SSPLv1 or AGPLv3. The AppHost declares `AddValkey("cache")` with the image's
  major pinned (`valkey/valkey:9`), the `redis` service of `docker-compose.yml` and the generated CI
  start the same image, and the API keeps its Redis client, which speaks to Valkey unchanged; the
  option, the `UseRedis` symbol and the `cache` resource keep their names. RedisInsight (SSPL) is
  gone, so the cache has no browser tile. `ContainerImageTests` holds every image the AppHost
  declares, the workflows run and the root compose file starts to an allowlist that names each one's
  license, so an image that is not free to ship fails the tests (ADR-0011, amendment of 2026-11-08).
  To move a 0.1.x solution: `Aspire.Hosting.Valkey` in place of `Aspire.Hosting.Redis` in the
  AppHost, `AddValkey(...).WithImageTag("9")` in place of `AddRedis(...).WithRedisInsight()`, and
  `valkey/valkey:9` with a `valkey-cli ping` health check in the compose file.
- **More packages the build refuses** (ADR-0011, amendment of 2026-11-08). `LicenseCeilingPackageIds`
  gains `Hangfire.Core` and `Hangfire.Pro` (LGPL-3.0 or commercial), `TickerQ` and `TickerQ.Utilities`
  (a source-available agreement from 10.5.0), `NBomber` (not free for organizational use),
  `Duende.IdentityServer` (commercial), `NServiceBus` (RPL-1.5 or commercial), and
  `MySql.EntityFrameworkCore` with `MySql.Data` (GPL-2.0): a direct reference, or one that arrives
  under another package, fails the build, Dependabot never proposes them, and the LICENSE CEILINGS
  comment in `Directory.Packages.props` gives each its reason. A family is refused through the package
  all its members depend on (`Hangfire.AspNetCore` through `Hangfire.Core`). The comment also names a
  ceiling the build cannot enforce: `Verify` from 33.0.0 ships a maintenance-fee agreement in its
  package, so pin it below 33 if you add snapshot tests. `LicenseCeilingTests` keeps the list, the
  comment and Dependabot's ignores naming the same ids.
- **A new project passes its own format check, whatever its name.** A project generated as `Acme.Shop`
  failed the Format check of its own `ci.yml` on the first push, with an IMPORTS error in nearly every file:
  `.editorconfig` set `dotnet_sort_system_directives_first`, which turns on `dotnet format`'s
  using-order check, and that order sorts by the full name, so the shipped files were in order only for
  a name that sorts where Trellis does (after Shouldly, before Xunit). The key stays in the template's
  own source behind the `TemplateSource` conditional, so a generated project starts without it: the
  IDEs still sort `System` usings first by default, and nothing checks the order until you opt in by
  adding `dotnet_sort_system_directives_first = true` to `.editorconfig`, restoring and running
  `dotnet format` once (the comment there says how). Template CI runs the generated `ci.yml`'s Format
  check on every leg, one of them named `Zeta.Journal` so a name that sorts after Xunit is covered, and
  `scripts/test-template.sh` does the same. The `finish-change` skill formats only the files the change
  touches and reports a formatter change anywhere else instead of committing it.
- **`/finish-change` loads in a headless session, and never commits on `main`.** A `claude -p` run had
  `/finish-change commit` refused outright while `add-feature` loaded: `finish-change` and
  `add-migration` were the two skills that pre-approved commands in an `allowed-tools` frontmatter, and
  neither does now (`.claude/settings.json` already allows the scripts and git commands they listed,
  apart from the OpenAPI snapshot run). The Commit box now stops when the branch cannot be created
  instead of committing on `main`.
- **`./scripts/ef.sh` works on a project nothing has built yet.** The first
  `./scripts/ef.sh migrations add …` in a fresh clone failed with NETSDK1004 (`project.assets.json` not
  found): dotnet-ef reads the project's metadata before it builds, and that needs a restore. The script
  now restores the solution first, quietly (a failed restore still prints its errors).
- **`/finish-change` tests the Release build, as CI does.** Its test boxes ran a plain `dotnet test`
  right after the Release build, which built the solution again in Debug and, with the UI, could stop on
  `Unable to find a project reference for project configuration item '…UI.Common.csproj'`. Every test
  box, the OpenAPI snapshot run included, now passes `-c Release --no-build` as `ci.yml` does, and so
  does `add-feature`'s last box; `docs/development.md` (*When the build fails*) explains the message.
- **Every unique index is decided on, and the persistence rule no longer goes stale.**
  `.claude/rules/persistence.md` named the registered unique constraints "today", so it was wrong after
  the first feature with a unique key, and an agent is not always allowed to edit `.claude/`. The map in
  `UniqueConstraintErrors` is now the list, and `UniqueConstraintTests` walks the model: a unique index
  or key that is neither registered there nor named a fault on purpose, with the reason no two requests
  can race to it, fails it, and so does a name the model no longer holds. A single-column uuid or
  database-numbered primary key needs no row; the walk cannot tell a uuid a request chooses from one
  minted for the row, so register or list the key of an aggregate whose id a client supplies yourself.
- **A change to the role grants owes the threat model.** `scripts/check-docs-drift.sh` asked for a
  `docs/threat-model.md` update when authentication, hardening or identity code changed, but not for
  `Application/Authorization/`, where `Roles.Permissions` decides who may call each endpoint, so granting
  a role a new permission went through unasked. That folder counts now, in CI and in the edit hook's
  reminder, and `AGENTS.md` says so.
- **The reviewer agents read right under your project's name.** `code-reviewer`, `test-reviewer` and
  `security-reviewer` described their work as reviewing "a Trellis change", which a project generated as
  `Acme.Shop` got as "a Acme.Shop change"; they review "a change to Acme.Shop" now, and Template CI
  fails a generated project with a sentence that reads "a <name>" after the rename.
- **`.claude/hooks/common.sh` is generated executable.** It was the one hook script without the
  executable bit, so the `chmod +x scripts/*.sh .claude/hooks/*.sh` that the template's instructions and
  `ci.yml` give left a mode change in a project generated a minute before. It ships executable, the
  chmod post-action lists it, and `scripts/check-hooks.sh` checks its mode like the hooks'.
- **A NUL character in a request is a 400, not a 500.** PostgreSQL's `text` stores no U+0000, so a body
  string holding one (`"Acme\u0000Ltd"`) reached the first query or save that carried it and came back
  500 `Server.Failure`: creating an invoice or adding a line, and signing in. `AddApiCommon` now
  registers `NulFreeStringJsonConverter` beside the strongly-typed-id converter, which refuses such a
  string while the body binds, on every endpoint, yours included, as 400 `Request.Malformed` whose
  `errors[]` names the member (`Request.InvalidValue`); responses are written as before, and so are the
  OpenAPI document's string schemas (`StringSchemaTransformer` gives `string` back the type the generator
  leaves out for a type with a converter of its own), while its `ProblemResponse` and `FieldError`
  descriptions now name a string holding U+0000 beside a value of the wrong type. `EmailAddress` also
  refuses a control character (`EmailAddress.Invalid`). A body string of a feature of yours needs no NUL
  check of its own, and a route value cannot carry one (Kestrel refuses `%00` in a path before routing,
  which `RequestLimitTests` pins); a string bound from the query reaches the handler as sent, so a
  `?search=` filter of yours refuses U+0000 in its query's validator, and
  `EndpointContractTests.Every_query_value_of_a_GET_refuses_a_NUL` fails on any GET that answers a NUL in
  its query with anything but a 400 (`.claude/rules/api.md`).
- **A create that loses a same-key race on a unique business key hears the idempotency answer.** Two
  concurrent requests under one `Idempotency-Key` were decided by the key's primary key only when both
  reached their commit; a feature of yours with a unique business key (an e-mail, a code) let the second
  fail on the first one's row before that, so it answered your feature's 409 (an e-mail already in use)
  for a create that succeeded. `IdempotencyFilter` now asks the store again when a keyed request fails
  with a 4xx and answers from the first request's reservation: 409 `Idempotency.InFlight`, the replayed
  201 or 409 `Idempotency.Duplicate` (ADR-0018, amendment of 2026-11-09). It costs one lookup, on failed
  keyed requests only.
- **A list of yours reuses the paged envelope instead of copying it.** `ToPagedResponse(map)` lived in
  the Invoices slice's `ContractMappings`, so a second list wrote its own copy, passing the envelope's four
  counts and two flags by position (a swapped `TotalCount` and `TotalPages` compiles), and removing the
  Invoices slice took the original with it. It is now `PagedResponseMappings` in Api.Common
  (`using Trellis.Api.Common.Http;`), written with named arguments; a slice maps only its item.
  `ConventionTests` fails on a `new PagedResponse<…>(…)` or a method declared to return one anywhere
  else under `src/`. If you copied the old mapping into a feature, delete the copy and call the shared one.
- **Your first migration shows your model change, not the template's EF version.** The shipped
  `InitialCreate` and model snapshot were written by EF Core 10.0.12 while the project pins 10.0.13, so
  the first `migrations add` in a new project rewrote the version line too. `InitialCreate` is
  regenerated on the pinned EF Core (its id is now `20261111173434_InitialCreate`, its `Up` and `Down`
  unchanged); upgrading a project whose database already applied the old one, keep yours
  (`docs/upgrading.md`, step 5). A migration id that contains one of the template's port literals (`17171`
  and the AppHost's other launch-profile ports, which the engine replaces wherever they appear) shipped with
  its `[Migration]` attribute rewritten under a file still named after the real id; a maintainer-only fact
  now refuses such an id and Template CI's content gate compares every generated migration's attribute with
  its file name. The `add-migration` skill now says why a project whose name sorts before
  `Microsoft` sees the snapshot's entity blocks move on its first migration (EF orders them by CLR name),
  and that moved blocks are not model changes. `migrations remove` is written with `--force` in
  `docs/development.md`, `docs/upgrading.md`, the `add-migration` and `upgrade-template` skills and the
  edit hook's hint: without it the command stops when no database answers its check, as in the SDK
  container. With it, a database the design-time connection reaches that applied the migration has it
  reverted first (`InitialCreate`'s `Down` drops every table), so the regenerate steps run it as
  `env -u TRELLIS_DESIGNTIME_DB ./scripts/ef.sh migrations remove --force …`, where none answers.
- **`docs/features/invoices.md` names the codes a client actually gets.** It listed
  `Invoice.EmptyCustomerName`, `Invoice.CustomerNameTooLong` and the line-description pair as the API's
  400s, but the command's validator checks the same Domain constants first, so through the API a blank or
  over-long name or description is `Validation.General` naming the field; the domain codes guard the
  callers without a validator (the seeder). The doc says so, `ProblemDetailsContractTests` pins the
  over-long name on the wire, and the `add-feature` skill asks a feature doc to list such a rule as
  `Validation.General` with the domain code in brackets.
- **Two ADRs stop listing what your first feature changes.** ADR-0006 said `EmailAddress` and `Currency`
  stay complex types, and ADR-0007 that Member holds `invoices:read`; a feature that converts a value
  object for its unique index, or grants Member its own read permission, made either false, and an
  accepted record only takes amendments. Each gains an amendment of 2026-11-09 pointing at the code:
  `AppDbContext.ConfigureConventions` lists the converted value objects, `Roles.Permissions` the role map.
  `.claude/rules/persistence.md` says converting a type is a migration and a line in the feature doc, with
  no ADR edit.
- **The test helpers a second feature needs are shared.** `ShouldAnswerAsync`, the status check that
  names the request, the problem `code` and the body when an arrange step is refused, was private to
  `InvoiceHttpDriver`; it is now `HttpResponseAssertions.ShouldAnswerAsync` in ApiTests, for every
  `{Feature}HttpDriver`. `StronglyTypedIdParsingTests` moved out of `Domain.UnitTests/Invoices/` and became
  one theory over every `IStronglyTypedId<>` the Domain declares (a version-7 `New()`, the parse round
  trip, text that is not a `Guid` turned down), so your IDs are covered with no test of their own and the
  check survives `remove-feature`.
- **Every list's paging bounds are checked at the wire, yours included.** A query's validator repeats
  `PagingLimits`' bounds as literals (Application cannot see the contracts), so each list re-tested them
  by hand. `EndpointContractTests.Every_paged_list_holds_its_documented_bounds_at_the_wire` reads every
  GET the served OpenAPI document gives a `page` and a `pageSize` and sends each bound and one past it,
  expecting a 200 inside (a 404 too for a nested list, whose route gets a fresh id) and a 400 naming the
  field outside: a validator whose literal drifts fails it, and a new list needs no bounds test of its own
  (`.claude/rules/api.md`, List endpoints). The GETs it reads are held to the GETs whose 200 is a
  `PagedResponse<T>`, so a list with a filter of its own beside the paging pair (`?status=`, `?search=`)
  is never left out unseen, and a list that answers the envelope without both parameters fails.
- **The UI has a design system** (`docs/design-system.md`, ADR-0032; with `--include-ui`). The values
  live in `wwwroot/tokens.css`, every colour once as a `light-dark()` pair in two themes, with the type
  scale, the spacing, the radii and the focus ring as tokens; `app.css` holds the components and writes
  no colour; `DesignSystemTests` proves the contrast of every text on its ground in both schemes, that
  only the token file writes a colour, and that the doc and the files name the same tokens and classes.
  The starting palette is a green ink and accent on near-white, a lime accent on near-black. Two tokens
  were renamed for anyone who customised 0.1.x: `--fg` is `--ink` and `--border` is `--line`
  (`docs/upgrading.md`, step 7).
- **The pages are built from the design system's components** (with `--include-ui`). The invoice
  list's status is a pill (Paid a success, Voided a danger, Draft and Issued neutral, the word always
  shown), as is the status on the invoice page; the pager's buttons are secondary buttons; one label
  strip styles the filter row's label and the table headers. On a phone the tables read as blocks,
  each cell led by its header: a table cell carries `data-th` with its header's text. A link without a
  class of its own (a table row's link, the sign-in pages' links) is accent text like the shell's,
  where it took the browser's default colour before. Two classes were renamed: `.notice` is
  `.callout success` and `.error-panel` is `.callout danger` (`docs/upgrading.md`, step 7).
  `docs/design-system.md` names the new components (`.label`, `.secondary`, `.pill` and its variants,
  `.callout` and its variants).
- **A theme switch the user keeps** (with `--include-ui`). The top bar ends with a System, Light and
  Dark select, there signed out too: Light or Dark is kept in the browser (`localStorage`, key `theme`)
  and written as `data-theme` on `<html>`, which holds that theme over the operating system's; System
  lets it go. `UI.Common/wwwroot/theme.js` sets the attribute from the web host's `<head>` before the
  first paint, so a kept Dark never flashes light, and is a file because the host's
  Content-Security-Policy runs no inline script; `Layout/ThemeToggle.razor` reads and writes it through
  `IJSRuntime`, never during a render. A `select` now looks like an `input`. `docs/design-system.md`
  names the component (`.theme`, `.theme-select`) and says how to remove it; a browser flow in the
  optional `UiTests` project proves the choice survives a reload and a link out of the error page and
  wins over a light system, and that a host without the script keeps working.
- **Screenshots at 400 and 1280 px in both themes, kept by CI** (with `--include-ui-tests`).
  `DesignSystemProofTests` in the optional `UiTests` project opens every page (sign-in, create account,
  the invoice list and its Overdue filter, a new invoice, an invoice) at both widths in light and dark,
  saves a full-page screenshot of each and fails a page that scrolls sideways; one more screenshot has
  Dark chosen in the switch over a light system, and one test tabs from the top of the page to the first
  top-bar link and checks it shows the system's focus ring, not the browser's own. The screenshots go to
  `UI_TESTS_SCREENSHOTS` (by default `TestResults/screenshots` under the test run's working directory),
  and the `ui-tests` job in `ci.yml` uploads them as the `ui-screenshots` artifact, kept 14 days, passed
  or failed. Nothing compares them with a baseline: fonts render differently per machine, so a reviewer
  looks (`docs/design-system.md`, *Proof*). The browser tests sign in through one helper,
  `Session.SignInAsAdminAsync`, and the proof's protected pages share one signed-in page: the API allows
  ten sign-ins a minute from the web host.
- **The design tokens export as JSON** for a design tool or a published design-system page:
  `./scripts/export-design-tokens.sh [-o tokens.json]` writes one object per token in `tokens.css`, in
  its order, with its value (a colour as its light and dark pair) and its usage, the token's *Role* in
  `docs/design-system.md` (*Mocks and the export*). It needs only `sh` and `awk`, and
  `DesignSystemTests` runs it and holds every name, value and usage to the token file and the doc. A
  project generated with `--include-ui false` gets the script too; it says there is nothing to export.
- **A `ui-reviewer` subagent** (`.claude/agents/`, with `--include-ui`) reads a change to a page, a
  layout or a stylesheet against the design system, the UI rules and accessibility, in a browser through
  the Playwright MCP when one is running, and reports; `.claude/rules/ui.md` asks for it before a UI
  change is finished and its accessibility rule now names the page title, the single `h1` and colour as
  never the only signal; the `add-feature` and `remove-feature` skills name the design system's
  components and tests in their UI steps.
- **Two more reviewers, and every reviewer says what it owns.** `test-reviewer` reads a change's tests
  against `.claude/rules/tests.md` (the tier, a bug fix's test shown to fail without the fix for the
  expected reason, `ShouldBeSuccess()`, the factories and drivers, time without sleeping, the fixtures,
  the narrowed run), and `security-reviewer` reads a change to authentication, identity, hardening, the
  Auth feature or who may call an endpoint against `docs/threat-model.md`. Every reviewer in
  `.claude/agents/` now opens with *Owns*, *Out of scope* and *Overlap* lines, so a change read by
  several gets each point once, and `AGENTS.md` says which reviewer a change needs. `docs/INDEX.md`
  gains sections for the reviewers, the hooks and the MCP servers (when to reach for Microsoft Learn,
  Context7 and Playwright, and the optional `dotnet/skills` plugins, none enabled);
  `scripts/check-adr-links.sh` fails when a reviewer, a hook or a feature doc is missing from it, or
  when `.claude/settings.json` runs a hook that does not exist. `architecture-guard`'s dependency
  flow now matches `AGENTS.md`, and ADR-0014 records all of this in an amendment.
- **An accepted ADR only gains amendments, and CI says so.** A new `adr-history` job in `ci.yml` runs
  `scripts/check-adr-history.sh` on every pull request: deleting or renaming a record under `docs/adr/`
  fails, and so does any edit to an existing one other than its header's *Status*, *Superseded*,
  *Amended* and *Refines* lines, a *Prior art* line it gains, and a dated `## Amendment (YYYY-MM-DD)`
  section (an ordinal such as `## Second amendment (…)` counts) appended at its end (ADR-0001's
  amendment of 2026-10-26).
  A template upgrade that appends amendments to the template's records passes. For a rewrite that is
  truly needed, create an `adr-rewrite` label in your repository and put it on the pull request, as
  with `docs-not-needed`.
- **More of the rules are tests now** (`tests/Trellis.ArchitectureTests`). The Domain, the use cases
  (event handlers aside) and the endpoints never `throw`; time comes from the registered `TimeProvider`
  and ids from their type's `New()`, never `DateTime.UtcNow` or `Guid.NewGuid()`; nothing silences RSLT001
  (no pragma, bare or naming it, `[SuppressMessage]`, `NoWarn`, analyzer or warnings-as-errors switched
  off, or lowered severity, a bulk one included; `.editorconfig` now names RSLT001's severity itself, so
  no bulk severity line reaches it); tests advance a clock instead of sleeping; every aggregate is
  created through a `static Create` returning `Result`, with no public static method that hands it back
  bare; every project under `src/` is anchored in `ArchitectureRules.cs`, ServiceDefaults included; and,
  with `--include-ui`, no page uses an endpoint `[Authorize]`, an `HttpClient` of its own, a POST of its
  own or an inline script. Each scan has a positive control and an allowlist that names its reason, and
  `.claude/rules/` names the test behind each rule.
  A `Console` write in a library project is now a build warning (S106), so a Release build fails on
  one; `async void` already was (S3168). One fix came with
  them: a `Retry-After` the API writes as a date now reads as the wait from the registered clock (the
  UI's `TimeProvider`), not the machine's.
- **Claude Code asks before the edits your conventions reserve, and commits in style.** Three new hooks
  in `.claude/hooks/`: `guard-edits.sh` asks you before an agent hand-edits a migration or
  `docs/api/openapi.v1.json` (the prompt names the command that regenerates it), creates a project or
  adds a package, and stays silent for a version bump; `guard-commit.sh` asks when a `git commit -m`
  subject is not `type(scope): subject` and, when gitleaks is installed, refuses a commit whose staged
  changes hold a secret; `stop-tests.sh` stops the agent once when it changed code under `src/` or
  `tools/` and ran no test (a subagent's edits and test runs count). The hooks work in the checkout
  Claude is in, a worktree included, and from a project path with a space, and a failed hook never
  blocks an edit or a command. The docs-drift reminder now speaks once per session for a given report.
  `AGENTS.md` gains a *Commits* line (a generated project had no commit convention before) and names
  the hook behind each boundary it backs; `docs/development.md` gains *Agent hooks* (what each decides,
  how to add one, how to turn them off); `scripts/check-hooks.sh` tests every decision, and CI runs it.
- **Finishing a change, upgrading the template, and a red build from a package.** The `finish-change`
  skill runs the gates CI runs, in its order (format, the Release build, the tests narrowed then whole,
  the OpenAPI snapshot, the docs-drift and link checks), asks for the CHANGELOG line and the reviewers
  the change needs, and commits or opens the pull request from `.github/PULL_REQUEST_TEMPLATE.md` only
  when asked (`/finish-change commit`, `/finish-change pr`). `/upgrade-template <version>` walks through
  `docs/upgrading.md` and never starts on its own: the marker, the changelog between the versions, the
  target regenerated with the same name and options, take, merge or keep, the schema, the marker last.
  `.claude/rules/dependencies.md` loads with the package and SDK files: asking before a package, the
  license ceilings, central versions and Dependabot groups, the order to answer a NuGetAudit advisory,
  and an SDK bump. `add-feature` points at *Protecting an endpoint* for who may call an endpoint.
  `docs/upgrading.md` installs the template as `<package>@<version>`, the separator current SDKs expect,
  and tells the files you never touched by regenerating the version you started from and diffing it
  against your tree: a diff against the target alone cannot tell your edit from the template's.
- **A check of your machine before the first run, and the running app for an agent to read.**
  `./scripts/doctor.sh` prints one line per prerequisite, `ok`, `missing` with the fix, or `skip`: a
  .NET SDK on `PATH` that `global.json` accepts (or that `./scripts/dotnet.sh` builds and tests without
  one), Docker and the SDK image, the Aspire CLI, the development certificate, the AppHost's ports, the
  engine endpoint the Testcontainers tests get, git, and jq where a script needs it. It changes nothing
  and exits 1 only when something a first run needs is missing; `--self-test` runs every check against
  fakes, and CI runs it (`docs/development.md`, *Prerequisites*). `.mcp.json` gains the Aspire MCP
  server (`aspire agent mcp`): while `aspire run` is up, an agent reads the AppHost's resources, logs
  and traces through it and can restart a resource. It needs the Aspire CLI 13.5 or later on `PATH`,
  and an interactive Claude Code session asks before using it (`docs/INDEX.md`, the `run-app` skill).
- **Aspire 13.6.5** (`Aspire.Hosting`, `Aspire.Hosting.AppHost` through the `Aspire.AppHost.Sdk` pin in the AppHost
  project, and every `Aspire.*` package with them): GHSA-f7vh-h5g3-7jrh (CVE-2026-120006, an elevation of
  privilege in Aspire, high) is published against every version below 13.6.5, and `NuGetAudit` turns it into
  NU1903, an error in every Release build, until the graph moves. The floors the new packages set move with
  them: `Microsoft.Extensions.Hosting` and `Microsoft.Extensions.Diagnostics.HealthChecks` to 10.0.14, and
  through the EF health check EF Core itself (`Microsoft.EntityFrameworkCore`, `.Design`,
  `Microsoft.AspNetCore.Identity.EntityFrameworkCore`) to 10.0.14, the version `dotnet-ef` already had. The
  shipped `InitialCreate` still reads `ProductVersion` 10.0.13; the next regeneration rewrites it.

## [0.1.1] - 2026-10-26

A patch: the documentation corrections and the two fixes that followed the first release.

- **CI installs exactly the SDK `global.json` names.** The workflows call `.github/actions/setup-dotnet`,
  which reads the version and installs that one from the release feed. Before, `setup-dotnet` honoured
  `rollForward` by installing the channel's newest release through aka.ms links, and a release Microsoft
  is still publishing answers 409 there: every job failed for hours on the day SDK 10.0.403 appeared.
  `global.json` still decides what a build accepts (`latestFeature`).
- **The request line is logged line-safe.** The malformed-request (400/413/415), unhandled-exception (500)
  and idempotency-residue log lines take the method and the path through `LogSafeRequestLine`, which
  replaces a line break with a space. On Kestrel nothing changes (a method is token characters only, the
  path is logged escaped); the point is CodeQL, which recognises the replacement as a sanitiser: the
  `codeql.yml` a generated project ships with no longer opens six `cs/log-forging` alerts on day one.
- **The docs read right on the website and on GitHub.** The Architecture page's dependency diagram says
  "Infrastructure and Persistence" in words (the ∥ sign has no monospace glyph) and its Persistence corner
  meets the line it was drawn to; the Invoices state diagram is laid out top to bottom, so it fits a
  1280 px screen without scrolling; the Invoices file tree keeps every comment within 80 columns. The
  website also gained a light theme built to the dark one's construction, wide reference tables that read
  as blocks, joined lines in box-drawing diagrams, and short headings with the folder path beneath them.
- **Dependencies**: .NET SDK 10.0.402 in `global.json` (the CI image carries 10.0.403); `Meziantou.Analyzer`
  3.0.311.

## [0.1.0] - 2026-10-25

The first release.

- **The shipped `InitialCreate` was regenerated.** `InvoiceLines.Amount` is `numeric(18,3)` (was
  `numeric(18,2)`), and the outbox's `NextAttemptAtUtc` is now a concurrency token (a snapshot-only
  change, no DDL). A deployed database needs a new migration on top for the column, never the
  regenerated one.
- **`Result.Error` is nullable** and `IsSuccess`/`IsFailure` carry `[MemberNotNullWhen]`: code that
  reads `result.Error.Code` without checking `IsFailure` no longer compiles (it used to answer a 500
  with an empty code). Guard the read.
- **The decorator pipeline order changed** to Tracing → Logging → Validation → UnitOfWork → handler.
  A decorator of your own registered by position should be re-checked.
- **New error codes** clients may branch on: 400 `Auth.RegistrationRejected`; 409
  `Invoice.AlreadyIssued` split off `Invoice.AlreadyFinalized`; `Money.AmountTooLarge`,
  `EmailAddress.TooLong`, `Invoice.CustomerNameTooLong` and `Invoice.LineDescriptionTooLong` (all 400).
  A registration race now answers 409 `Auth.EmailAlreadyInUse` instead of 500.
- **Dashboards**: the use-case span attributes are `trellis.usecase.outcome`,
  `trellis.usecase.error_code` and `trellis.usecase.error_type`, and the outcome log line changed
  shape (see *Changed*).
- **`POST /invoices` requires an `Idempotency-Key`** (400 `Idempotency.KeyRequired` without one); the
  writes to an existing invoice still accept it optionally. A repeated header or a comma-separated list
  is 400 `Idempotency.InvalidKey` instead of being joined into one key the caller never sent.
- **Two new `ErrorType`s change statuses.** `ExchangeRate.Unavailable` is a **503** (was 500) and a
  refusal by the provider is the new 500 `ExchangeRate.Rejected`; a stale `If-Match` on an invoice
  write is **412** `Invoice.VersionMismatch`. A client that branched on 500 for the outage should
  branch on 503, and a decorator or classifier of your own that switches over `ErrorType` has two more
  values, `Unavailable` and `PreconditionFailed`. On the outcome line a 412 is Information like the other
  expected failures and `Unavailable` is a Warning, so an alert on Warning still means a dependency is down.
- **A Release build fails on a discarded `Result`** (RSLT001, from the new analyzer project
  `tools/Trellis.Analyzers` that `Directory.Build.props` loads into every project, the tests included): a
  dropped `invoice.Issue(…)` or `Result.Combine(…)`, an awaited `handler.Handle(…)`, a forgotten `return`
  before `InvoiceErrors.X`, an unchecked `IdentityResult` or UI `ApiResult<T>` (in `.razor` files too), an
  `await Task.WhenAll(…)` over handlers, and a `Task<Result>` returned as a plain `Task`
  (`public Task Handle(…) => port.PushAsync(…)` in an event handler) are errors. Handle it or write
  `_ = …` with a reason; in a test, end the step in `ShouldBeSuccess()` (see *Added*). Adopting this
  version adds that project to your solution and two pinned build-only packages.
- **`GET /invoices/{id}` emits a strong ETag** (`"42"`, was `W/"42"`); `If-None-Match` still compares
  weakly, so a stored tag keeps answering 304.
- **The audit columns hold the user id.** `CreatedBy` and `ModifiedBy` are stamped with the acting
  user's id instead of their e-mail; the migration job still writes `system`. There is no migration:
  rows written before keep the e-mail they were stamped with, so a report that matched the columns
  against an address has to handle both, and an erasure request still finds the old rows by e-mail.
- **Log filters on the outcome line's category change.** Each use case now logs under its own
  command or query type (`Trellis.Application.Features.Invoices.ListInvoices.ListInvoicesQuery`)
  instead of `Trellis.SharedKernel.Decorators.LoggingDecorators.CommandHandler`/`.QueryHandler`. A
  `Logging:LogLevel` or `Serilog:MinimumLevel:Override` entry, or a log query, that named the decorator
  category no longer matches: key it on a feature's namespace, or match the event ids 1001–1099.

### Added

- **SMTP e-mail** (`Email:Provider=Smtp`; idea from
  [amantinband/clean-architecture](https://github.com/amantinband/clean-architecture)): a sender on
  the BCL `SmtpClient`, configured by an `Email:Smtp` section (`Host`, `Port` 587, `UseStartTls` on,
  `FromAddress`, and `UserName`/`Password` together or not at all) that is validated at boot only
  while selected (port 465 and credentials without STARTTLS are refused). A failed send is thrown so
  the outbox retries it (delivery is at least once). Events 1205 `EmailSent` (masked recipient) and
  1206 `EmailSmtpInClear` (a Warning when `UseStartTls` is off outside Development).
  `docs/development.md` has the user-secrets commands and a Mailpit recipe; implicit TLS on 465 and
  OAuth2 are not supported.
- **A "Try it" walkthrough** (idea from amantinband/clean-architecture):
  `src/Presentation/Api/Trellis.Api/Trellis.Api.http` walks the invoice lifecycle in twelve requests
  (sign in, create with an `Idempotency-Key`, add a line, issue, conditional GET and 304, pay, a second
  issue with the ETag from before the payment answered 412, the idempotent replay, register and sign in
  a member, the member's 403), with `http-client.env.json`
  one folder above the project so no build or image copies it. `docs/features/invoices.md` has the
  matching step table and now opens with the domain in plain words, a state diagram and an annotated
  tree of the slice.
- **Results kernel** (ideas from [xavierjohn/Trellis](https://github.com/xavierjohn/Trellis)):
  `Result.Combine` merges into a `ValidationError` only when every failure is one and otherwise
  returns the first failure unmerged (two 404s no longer answer 400); `Combine<T1,T2>` and
  `<T1,T2,T3>` keep their values for a deconstructing `Bind`/`Map`; `MapOnFailure` replaces a
  failure's error; `Result.AtField(member)` attributes a value-object or aggregate-rule error to a
  request field, so `"a@b"` lands under `customerEmail` on the wire and on the form; reading `Value`
  on a failure throws naming the error's code (never its description).
- **Telemetry**: a `trellis.validation.failures` counter on the `Trellis.UseCases` meter, by use case
  and code; the outcome lines are `[LoggerMessage]` events 1001–1006 (succeeded, failed, failed
  validation, failed with detail, cancelled, threw); `UseCaseLoggingOptions.IncludeErrorDetail` (off
  by default; a host that wants it in Development binds the `Telemetry` section) puts the error
  description back on the line.
- **Persistence**: `IScalarValueObject<TSelf, TValue>` with `ScalarValueObjectConverter`, the mapping
  for a single-value value object that needs an index — a converted column instead of a complex type
  (ADR-0006 amendment; `EmailAddress` and `Currency` stay complex types); `AggregateConcurrencyInterceptor`
  marks the root modified when only its children changed, so the root's `xmin` is checked and bumped
  (idea from xavierjohn/Trellis); `UniqueConstraintErrors` maps constraint names pinned in
  configuration to errors.
- **Domain**: `Currency.MinorUnits` (ISO 4217: 0 for JPY, 3 for KWD and the other dinars) and
  `Currency.CodeLength`; `Money.MaxAmount`, `Money.AmountPrecision` and `Money.AmountScale`;
  `Invoice.CustomerNameMaxLength` (200), `InvoiceLine.DescriptionMaxLength` (500) and
  `EmailAddress.MaxLength` (254) as the one place each limit lives — the validators, the EF
  configurations and the UI forms use them.
- **Security events** 1105 `FailedSignInNotCounted` and 1106 `FailedSignInCountNotReset` (Warning,
  user id and Identity error codes): the lockout counter's writes report failure instead of being
  discarded.
- **Generated CI**: a line-rate floor per ring (Core 90 %, Infrastructure 85 %, Presentation 75 %,
  Shared 80 %) with a per-ring table; a TRX summary in the job summary, failures first; a
  `container-smoke` job that publishes the API and migration images, runs the migration against
  PostgreSQL and boots the API in Production with only the documented settings, probing `/health`,
  an anonymous 401 problem document and a seeded sign-in (the web image too with `--include-ui`,
  Redis with `--use-redis`); `scripts/check-adr-links.sh` also resolves every relative Markdown link;
  `.gitattributes` gives C# diffs hunk headers that name the member.
- **Aspire dashboard**: a "Scalar" link on the `api` resource; with `--use-redis`, a RedisInsight tile
  beside the `cache` resource, as pgAdmin sits beside PostgreSQL.
- **AppHost launch profiles**: `tools/Trellis.AppHost/Properties/launchSettings.json` with an `https`
  profile (dashboard at `https://localhost:17171`) and an `http` one (`http://localhost:15171`), so
  the AppHost runs from Rider's and Visual Studio's Aspire run configuration, which refuses to start
  without a profile. `dotnet run` and `aspire run` pick the `https` profile too instead of a dynamic
  dashboard URL. An `appsettings.json` beside it keeps the dashboard's own request logs out of the
  AppHost console (`Microsoft.AspNetCore` and `Aspire.Hosting.Dcp` at Warning), as the Aspire
  starter template does. Each generated solution gets its own six ports, picked free at `dotnet new`
  time (`port` generator symbols, as the Aspire starter does), so two solutions run side by side.
- **Template repository** (not in generated projects): a Credits section in the README and the rule
  that keeps it current, a package icon and wider tags, a Discussions link for questions, and a
  Windows leg in Template CI. The README is now a walkthrough in the style of
  amantinband/clean-architecture (credited there): the domain before the architecture, a request's
  path, the toggles, and pictures of the rings, the tree, that path and the test suites, drawn from
  the repository by `eng/readme/render-diagrams.py` (light and dark; CI fails when they go stale).
- **README pictures restyled** (not in generated projects): a dark banner with the install commands,
  the solution as a bento of ring cards holding their projects, the test suites on a rail of the layers
  and a request as fifteen numbered steps, in the visual language `eng/readme/README.md` describes; the
  README opens with a centred hero.
- **503 `Unavailable` and 412 `PreconditionFailed`** (ideas from
  [xavierjohn/Trellis](https://github.com/xavierjohn/Trellis)): `ErrorType.Unavailable` with
  `Error.Unavailable(code, description)` is a dependency that is down for now; `CustomResults.Problem`
  answers **503** "Service unavailable" with a fixed `detail` ("A service this request depends on is
  unavailable right now. Retry later.") and the error's `code` (no `Retry-After`: the adapter's retry
  budget is spent by then). `ErrorType.PreconditionFailed` with `Error.PreconditionFailed(code,
  description)` answers **412** "Precondition failed" with the error's own description.
  `ProblemDetailsContractTests` has a row for each.
- **Conditional writes** (xavierjohn/Trellis): `AddInvoiceLine`, `IssueInvoice`, `PayInvoice` and
  `VoidInvoice` take an optional `If-Match` carrying the ETag the `GET` emitted
  (`.SupportsConditionalWrite()` declares the header and the 412 in the OpenAPI document; the commands
  gained `uint? ExpectedVersion`, compared by `InvoicePreconditions.ToWrite`). A stale, weak, listed,
  zero-padded or foreign tag is 412 `Invoice.VersionMismatch` that changes nothing: a tag names a version
  only in the exact form the `GET` emitted (`"42"`, never `"042"`), and a malformed header is refused
  before the lookup, so it never tells whether the invoice exists; `*` or no header is unconditional
  (no 428); a well-formed tag on a missing invoice stays a 404. The Blazor detail page sends the version
  it loaded and shows "Someone else changed this in the meantime" over the reloaded invoice on a 412.
- **One permission per endpoint, pinned** (idea from amantinband/clean-architecture):
  `EndpointMetadataTests.ExpectedPermissions` lists each endpoint's one permission beside
  `AnonymousAllowlist` and fails on a missing, extra or stale row; a wire-level theory calls every
  endpoint with only its permission (its own status) and with every other one (403 `Auth.Forbidden`).
  `ApiFactory.CreateClientWithPermissions(params string[])` mints a token through the host's
  `ITokenProvider` for tests of your own.
- **Fitness tests** (ideas from xavierjohn/Trellis): `ErrorCodeVocabularyTests` reads every
  `Error.<Type>(` factory, `.WithErrorCode(` and `ProblemCatalog` row under `src/` and `tools/` and
  requires the `Area.Problem` shape, one meaning per code and no code shared between a catalogue row
  and an `*Errors` class; `ConventionTests` refuses a handler that injects another `ICommandHandler` or
  `IUnitOfWork` (a use case never runs another one) and any handler outside Application; a value
  object may have neither a public constructor nor a public `set`/`init`;
  `CommandValidatorConventionTests` fails a `Must`/`MustAsync` without `.WithErrorCode(…)` (it would
  reach clients as `Validation.Predicate`); `OptionsConventionTests` boots the API with
  `Email:Provider=Smtp` and one broken relay setting per row. Every new rule has a positive control over
  a planted violation (`PlantedViolations`), named without `Invoice` so `remove-feature` leaves it alone.
- **RSLT001: a discarded `Result` fails the build** (idea from xavierjohn/Trellis, re-implemented;
  ADR-0030). `tools/Trellis.Analyzers`, a netstandard2.0 analyzer every project loads from
  `Directory.Build.props`, reports a statement that drops a SharedKernel `Result`, `Result<T>` or
  `Error`, the UI's `ApiResult<T>` or an `IdentityResult`, or an array, sequence, tuple or task of one:
  a call, an awaited `Task<T>` or `ValueTask<T>`, a conditional access, an object creation, `Wait()`, a
  void expression body, an unawaited task where CS4014 is silent, and a collection initializer's `Add`.
  It also reports a computed `Task<Result>` converted to a plain `Task`, the shape that let an outbox
  handler on a `Result` port checkpoint a failed push as done. Razor `@code` blocks are analysed. An
  assignment (`_ = …`), a return, an argument, generated code, NSubstitute's `Received()` checks, a
  chaining assertion (`ShouldBeOfType<T>()`), the body of `Should.ThrowAsync(() => …)`, a call that
  hands back a held result (`Tap` on a local, `Stack.Pop`, `Interlocked.Exchange`) and expression
  trees are not reported; ADR-0030 lists the gaps. It matches by type and by a namespace ending in
  `SharedKernel.Results` (or `UI.Common.Services`), so it follows the project name.
  `Microsoft.CodeAnalysis.CSharp` 5.0.0 and `Microsoft.CodeAnalysis.Analyzers` 3.11.0 are pinned,
  build-only; Dependabot proposes only their patch updates, because the referenced Roslyn must stay at
  or below the compiler of the SDK in `global.json`. `ResultDiscardAnalyzerTests` cover each shape, the
  wiring and a planted discard compiled under the repository's `.editorconfig`. Its first run fixed
  nineteen drops in the tests (twenty-two with the multitenancy-only fixtures): identity seeding in
  `ApiFactory`, `AuthSecurityTests` and `TenancyTests` that never checked an `IdentityResult`, a handler
  test, and the `UseCaseOutcomeTests` that never looked at the outcome they caused; three UI client
  tests that never checked their `ApiResult<T>` followed.
- **A body value of the wrong type names its field**: `"amount": "abc"` is still 400 `Request.Malformed`,
  now with `errors: [{ field: "lines[0].amount", code: "Request.InvalidValue", description: "The value
  is not of the type this field expects." }]`; a JSON syntax error names no field.
- **Tests**: `UniqueConstraintTests` (an unregistered unique violation still throws out of
  `CommitAsync` and writes nothing, beside the registered one that maps to a `Result`);
  `InvoiceFormMirrorTests` (the UI forms' lengths equal the Domain constants);
  `UseCaseLoggingOptionsTests`; `ShouldHaveMediaType`/`ShouldBeProblemJson` assertions that fail when
  `Content-Type` is missing (the `?.MediaType` form passed when both responses lacked the header).
- **Test helpers** (ideas from [amantinband/clean-architecture](https://github.com/amantinband/clean-architecture):
  use-case drivers, optional-parameter factories, the paired HTTP client and a switchable current user,
  rewritten without MediatR; the failed `Result` that names its code from xavierjohn/Trellis):
  `ShouldBeSuccess()` (`tests/TestSupport/ResultAssertions.cs`, compiled into the four test projects
  with a global `using Trellis.TestSupport`) asserts a `Result` succeeded, returns a `Result<T>`'s value,
  and on failure names the asserted expression and the error's code, type, description and field errors;
  `InvoiceFactory` (`tests/TestSupport/Invoices/`, namespace `Trellis.TestSupport.Invoices`, compiled into
  Application.UnitTests and IntegrationTests) builds valid commands (`CreateCommand`, `AddLineCommand`,
  `IssueCommand`) and aggregates (`Draft`, `DraftWithLine`, `Issued`, `Paid`) with an optional parameter
  per value and replaces `InvoiceTestData`; `InvoiceDriver` (IntegrationTests; `IServiceProvider`
  extensions `CreateInvoiceAsync`, `AddInvoiceLineAsync`, `IssueInvoiceAsync`, `PayInvoiceAsync`,
  `VoidInvoiceAsync` and the `IssuedInvoiceAsync` shortcut) runs each use case through the real
  pipeline, asserts success and returns the id; `InvoiceHttpDriver` (ApiTests) is the same steps as
  `HttpClient` extensions, failing with the method, path, status, problem code and body;
  `TestCurrentUser` (`IntegrationTestBase.CurrentUser`) is the mutable `ICurrentUser`, so a test acts
  as a second user. Drivers are for arrange steps only, one method per use case, no generic
  `SendAsync`; `tests/TestSupport/` is linked source, not a project (ADR-0009 amendment).
- **Tests**: `VoidInvoiceCommandHandlerTests` (not found, a draft voided with its event, a paid invoice
  refused unchanged, the `If-Match` version paths) and `InvoiceCommandValidatorTests` (every bound at
  its Domain constant: the maximum accepted, one more rejected naming the property; the amount floor,
  the required due date, the overdue list's paging bounds). The Application tests mirror the slice, one
  folder per use case under `Application.UnitTests/Features/Invoices/<UseCase>/` (the
  one-test-suite-per-use-case discipline from amantinband/clean-architecture). `UseCaseOutcomeTests`
  pins the level of `PreconditionFailed` and `Unavailable`; `InvoiceContractTests` proves the
  `private, no-cache` opt-in and the zero-padded `If-Match`; `TelemetryNamingTests` holds the
  Telemetry reference in `docs/deployment.md` to the code, so a meter, source, instrument, attribute or
  `[LoggerMessage]` event id without a row fails the suite.
- **Docs, rules and skills** (shapes from amantinband/clean-architecture where named):
  `docs/deployment.md` gains the Telemetry reference (spans, metrics and the event-id ranges), trace
  sampling, image tags and an SBOM; `docs/development.md` the testing map by project and layer (now the
  one the other docs point to; amantinband's Testing section), the Microsoft.Testing.Platform filter
  syntax for running part of the suite, which helper serves which goal, and "When a request fails";
  `docs/architecture.md` the pipeline stage by stage, domain versus integration events with their
  delivery guarantees, scheduled use cases and package validation for a shipped `Api.Contracts`;
  `docs/features/auth.md` "Protecting an endpoint" with the ownership recipe (amantinband's authorization
  how-to) and an external identity-provider checklist; `docs/features/invoices.md` the path from a
  payment to its receipt (amantinband's eventual-consistency walkthrough) and the 412 step of the
  walkthrough; the README shows the code in three excerpts (the handler, the aggregate, the endpoint). The rules gain handler rules, multi-aggregate
  commands, the lifecycle rule and an `ErrorType` chooser (`application.md`, `domain.md`), the fitness
  suites and their controls (`tests.md`), the idempotency-key rules and the cache opt-in (`api.md`); the
  skills a webhook recipe (`add-integration`), a spec-first step and a keyset-paging recipe
  (`add-feature`), the invoice test files to retarget (`remove-feature`) and an optional
  *Alternatives / Revisit when* section (`new-adr`); `AGENTS.md` three more *Always* lines. ADR-0001,
  0003, 0004, 0007, 0009, 0018, 0021, 0025, 0026 and 0028 carry dated amendments.
- **Docs**: `docs/INDEX.md` lists every skill under *Skills*, and `check-adr-links.sh` fails when one is
  missing; the persistence rule names the lease fence and handler isolation, the infrastructure rule the
  SMTP sender and its selected-only validation; a threat-model row for the SMTP relay credentials;
  `docs/features/exchange-rates.md` carries the probed Frankfurter status table.
- **Dependency bumps that merge themselves**: `.github/workflows/dependabot-auto-merge.yml` enables
  GitHub's auto-merge on Dependabot's patch and minor pull requests once the checks pass; a major,
  a .NET SDK bump and the GitHub Actions group wait for you. `dependabot.yml` holds new versions back
  for a week (`cooldown`; security updates arrive at once). The workflow acts only when the repository
  allows auto-merge and a ruleset on `main` requires status checks, and leaves a notice otherwise.
- **`GET /api/v1/invoices` lists every invoice**, newest first, from the moment it is created: an
  invoice you create and issue is due in the future, so until now it appeared in no list (the overdue
  list was the only one). Paged like the overdue list (`page`, `pageSize`, the `PagedResponse`
  envelope), with an optional `?status=Draft|Issued|Paid|Voided`; `invoices:read`, so members see it
  too. `InvoiceDto` gains `createdAtUtc`, the key the list orders by: code that constructs it
  positionally passes the new member after `Status`. The `ListInvoices` use case, its specification
  (`InvoiceSpecs.All`) and its tests are in the reference slice to copy.
- **The web UI's Invoices page lists every invoice** (it was "Overdue invoices") with a Created
  column and a filter row, All, Draft, Issued, Paid, Voided and Overdue, kept in the address
  (`invoices?status=Draft`) so the browser's Back returns to the previous filter. The invoice and
  new-invoice pages open with a "Back to invoices" link.
- **The caller's user id on every request signal** (ADR-0028 third amendment;
  jasontaylordev/CleanArchitecture): `UserTelemetryMiddleware`, right after `UseAuthentication`, puts a
  signed-in caller's user id in a `CallerUserId` log scope, which the use-case outcome line and anything
  a handler logs inherit, and on the request span as `enduser.id`, the OpenTelemetry semantic
  convention. A 403 from a missing permission writes no log line at the shipped levels, so its span is
  what names the caller. The id only, never the e-mail; a request without a valid token carries neither.
  A project of your own that rebuilt the API's pipeline adds `app.UseUserTelemetry()` after
  `app.UseAuthentication()`.
- **An opt-in slow-use-case Warning** (ADR-0028 third amendment; jasontaylordev/CleanArchitecture):
  set `Telemetry:SlowUseCaseThreshold` (a TimeSpan, 1 ms to 1 hour, validated at boot) and a use case
  that takes at least that long writes event 1007 `UseCaseSlow` at Warning after its outcome line, with
  its name and elapsed milliseconds only. Unset by default, so nothing changes until you name a
  threshold. The API now binds the whole `Telemetry` section through the options pattern
  (`ValidateDataAnnotations().ValidateOnStart()`), and `OptionsConventionTests` polices
  `UseCaseLoggingOptions` like every other options class.
- **A one-host deploy quick start** at the top of `docs/deployment.md`, the short, ordered page
  jasontaylordev/CleanArchitecture's documentation keeps: build the images tagged with the commit,
  `aspire publish -o deploy`, fill `.env`, `docker compose up -d` (or `aspire deploy`), then probe
  `/health` and sign in, each step linking the reference section that explains it.
- **The API's root opens its reference in Development**: `GET /` redirects to `/scalar/v1`
  (jasontaylordev/CleanArchitecture), so the Aspire dashboard's endpoint link and a bare address land on
  the API reference instead of a 404. It sits outside `/api/` and out of the OpenAPI document, and
  outside Development the root stays a 404 (`ApiRootTests`).
- **`scripts/test-template.sh`** (not in generated projects): Template CI's legs on a maintainer's
  machine before the push. It reads the legs from `template-ci.yml`'s matrix, packs the template once,
  installs it into a private hive, then instantiates, builds and tests each named leg (or every leg),
  prints a table of results and exits 1 on any failure (idea from
  [jasontaylordev/CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture)'s `build/test.ps1`).
  `CONTRIBUTING.md` and the `add-toggle` skill use it instead of the manual recipe.
- **Release notes on nuget.org** (not in generated projects): the package's release notes link to the
  GitHub release of its version, whose notes are that version's CHANGELOG section
  (`PackageReleaseNotes` in the pack project; idea from
  [jasontaylordev/CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture)).
- **A documentation website** (not in generated projects): <https://revazi1st.github.io/Trellis/>,
  Astro + Starlight in `site/` rendering `docs/` in place, with a landing page, an Install and create
  page, an Options page generated from `template.json` and the decision records in the sidebar; a
  passage only some options bring is labelled with the option. Every link and anchor is checked on each
  pull request that touches the docs, the site makes no third-party request, and each release deploys it
  from its tag (a manual preview says so on every page). The package's project link points at it
  (ADR-0031; ideas from cleanarchitecture.jasontaylor.dev). `check-template-toggles.sh` now requires
  every directive to stand alone on its line.

### Changed

- **Pipeline**: Tracing wraps Logging (Tracing → Logging → Validation → UnitOfWork → handler), so
  the outcome line carries the use-case span's id instead of the HTTP request's (xavierjohn/Trellis).
- **Outcome line**: a failure reads `with {ErrorType} ({ErrorCode})`, a `ValidationError` keeps its
  field:code pairs, and `Error.Description` appears only with `IncludeErrorDetail`
  (xavierjohn/Trellis). The use-case span attributes are `trellis.usecase.outcome`,
  `trellis.usecase.error_code` and `trellis.usecase.error_type`; `error.type` is set only on a crash,
  to the exception's type name, so backends count failures where they count errors.
- **Outbox** (xavierjohn/Trellis): `NextAttemptAtUtc` is the row's concurrency token, so every
  bookkeeping save is conditional on the lease — a processor that outlived its lease books
  `lease_lost` (`trellis.outbox.outcome`, Warning 1303) instead of overwriting what the next claimant
  wrote, and a batch stops before a message whose dispatch budget would outlive the lease (Warning
  1306, leases handed back). Every uncredited handler runs on every attempt and only the failed ones
  are retried (Warning 1304 per handler; several failures surface as one `AggregateException` naming
  them); the retry and dead-letter counters and log lines fire only after their save succeeded. Every
  outbox line has an event id 1301–1309 (`docs/deployment.md` lists them), the dispatch span's
  `error.type` names the failing exception, and the events of one save are claimed in the order
  they were raised (`OccurredAtUtc` spaced by a microsecond each, `Id` as the tiebreaker).
- **Domain**: `Invoice.Issue` on an issued invoice answers 409 `Invoice.AlreadyIssued`;
  `Invoice.AlreadyFinalized` is for paid and voided ones. `Money` and `Invoice` hold the `Currency`
  value object (same columns); `Money` rejects amounts finer than the currency's minor units
  (`Money.TooManyDecimalPlaces` names the limit) instead of rounding, and amounts over
  `Money.MaxAmount`; `InvoiceLines.Amount` is stored as `numeric(18,3)`. Customer name and line
  description are trimmed and bounded in the Domain, not only in the validators, so the seeder cannot
  bypass them. `EmailAddress` allows at most 254 characters and 64 before the `@`, refuses leading,
  trailing or doubled dots and empty or hyphen-edged labels, and still accepts internationalized
  addresses.
- **Validation errors** from value objects and single-rule aggregate methods carry their field
  (`customerName`, `customerEmail`, `currency`, `amount`, `description`, `dueDate`) instead of
  `field: null`; a root-level FluentValidation rule (`RuleFor(x => x)`) is a whole-request error,
  `field: null`, no longer `field: ""` (xavierjohn/Trellis).
- **Registration**: an e-mail or password Identity refuses is 400 `Auth.RegistrationRejected`, one
  fixed sentence per Identity error code; `Auth.RegistrationFailed` is only the 500 of a failed role
  rollback. The auth validators bound the e-mail at 254 (was 320).
- **Migration job**: a sample invoice whose transition fails stops the run with exit 1 instead of
  saving a half-built invoice.
- **HTTP contract** (ADR-0021 and ADR-0024 amendments): every `/api/` response carries
  `Cache-Control: no-store`, problems included, unless the endpoint sets its own (`/auth/` is `no-store`
  whatever it sets); `GET /invoices/{id}` is the one opt-out, `private, no-cache` on its 200 and 304, so
  a browser keeps the body and revalidates with the ETag (its 404 stays `no-store`); every 401 carries
  `WWW-Authenticate: Bearer`, the Result-produced ones from login and refresh included.
- **Exchange rates** (xavierjohn/Trellis): the reference client maps the provider's status by the
  endpoint's contract (Frankfurter was probed): 422 and 400 → `ExchangeRate.UnknownCurrency` naming
  both codes (the provider's text is never copied); 404, 401 and 403 → `ExchangeRate.Rejected`, logged
  at Error as a misconfiguration; 408, 429 and 5xx after the retries, a timeout, an open circuit or
  the limiter → `ExchangeRate.Unavailable` (503); an unreadable body → `ExchangeRate.MalformedResponse`
  (500). The `add-integration` skill says to probe and map a new provider the same way.
- **`Telemetry:IncludeErrorDetail`** is bound by the API host and is `true` only in
  `appsettings.Development.json`: the error description is on the outcome line in Development and off
  everywhere else unless you set it.
- **Outcome line levels** (ADR-0028 second amendment): a stale `If-Match` (`ErrorType.PreconditionFailed`)
  is a classified expected failure logged at Information; `ErrorType.Unavailable` stays at Warning beside
  `Failure`.
- **One owner per `Idempotency.*` code** (ADR-0018 second amendment): `IdempotencyErrors`
  (`Trellis.Application.Abstractions`) now declares `MultipleKeys` and `KeyRequired` too, and
  `Idempotency.InvalidKey` once as `IdempotencyErrors.InvalidKeyCode`, shared by `BlankKey`, `KeyTooLong`
  and `MultipleKeys`; `Api.Common`'s `IdempotencyHeaderErrors` is gone. `RefreshTokenStore` returns
  `AuthErrors.InvalidRefreshToken` instead of a copy of it.
- **Audit columns** (ADR-0027 amendment; jasontaylordev/CleanArchitecture): `AuditingInterceptor` stamps
  `CreatedBy` and `ModifiedBy` with `ICurrentUser.UserId`, falling back to the name only for a caller
  without an id (`system`), so the columns no longer copy each user's e-mail into every row they touch.
  ADR-0007's 2026-10-11 amendment said a missing user id breaks auditing; it never did, and its
  2026-10-18 amendment says what a missing id does now.
- **A log category per use case** (ADR-0028 third amendment; jasontaylordev/CleanArchitecture): the
  logging decorators take `ILogger<TCommand>`/`ILogger<TQuery>`, so each outcome line is logged under its
  use case's type and `Logging__LogLevel__Trellis.Application.Features.Invoices=Warning` quiets one
  feature's success lines (`docs/deployment.md`, *Log levels per feature*). The unit tests'
  `LogCapture.OutcomeLines` and `SerilogExportTests` match the use-case event ids instead of the category.
- **Aspire resource names live in `ServiceNames`** (ServiceDefaults), the one place they are spelled:
  the AppHost declares the database and every service through it, referencing ServiceDefaults with
  `IsAspireProjectResource="false"` (jasontaylordev/CleanArchitecture), and the API, the migration job,
  the web host and the tests find a resource through the same constant (`ServiceNames.Cache` with
  `--use-redis`). A renamed resource is one edit that compiles everywhere; a mistyped literal used to
  compile and fail at boot.
- **PostgreSQL keeps running between runs**: the AppHost gives `postgres` a persistent lifetime
  (Aspire's `WithPersistentLifetime`, as jasontaylordev/CleanArchitecture's AppHost applies it), so
  `aspire run` and F5 find the database started and healthy instead of waiting for a new container; the
  data volume is unchanged and pgAdmin stays session-scoped. The Aspire E2E test strips the lifetime with
  the volume, so it never reuses or recreates your database: the step is `appHost.UseThrowawayDatabase()`
  (`ApiTests/AppHostTestingExtensions.cs`), which `remove-feature` keeps, so an E2E of your own starts
  with it too. For a fresh database, remove the `postgres-…` container and the `…-postgres-data` volume
  (`docs/development.md`).
- **Visual Studio starts the AppHost**: `Trellis.slnx` marks it `DefaultStartup="true"`
  (jasontaylordev/CleanArchitecture), so the first F5 after opening the solution runs the whole app
  instead of failing on `Trellis.SharedKernel`, the first project and a class library. Rider still wants
  the AppHost picked by hand.
- **The `web` resource has a health check**: the AppHost polls the web host's `/health`, as it does the
  API's, so `web` turns healthy in the dashboard, and for anything that waits on it, once it serves
  rather than once its process starts. The Aspire E2E test waits for it.
- **What `aspire publish` writes**: the signing key and the first administrator's password are
  parameters the AppHost declares in publish mode only, so the compose file reads them from `.env`
  (`SIGNING_KEY`, `SEED_ADMIN_PASSWORD`) instead of needing hand edits, and `aspire publish` and
  `aspire deploy` ask for them unless the AppHost's configuration holds them
  (`Parameters:signing-key`, `Parameters:seed-admin-password`). PostgreSQL, the API and the web host
  get `restart: unless-stopped` (the cache too, with `--use-redis`), and the Aspire dashboard's UI is
  published on the host's loopback only (`127.0.0.1:18888`, reached over SSH) instead of on every
  interface. Publish an existing `deploy/` folder again to pick these up: `aspire publish` keeps the
  values already in `.env`.
- **The test databases run Debian `postgres:18`**, the image the AppHost, `docker-compose.yml` and a
  deployment run, instead of `postgres:18-alpine` (`DatabaseFixture`, `ApiFactory`): Alpine's musl libc
  collates text differently from glibc, so a text sort could pass in a test and differ in production.
  The first test run pulls the larger image.
- **Dependabot leaves the next .NET major to you**: `dependabot.yml` ignores major updates of the
  packages numbered after .NET (`Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore*`,
  `Microsoft.Extensions.*`, `Npgsql.*`, `dotnet-ef`, `Asp.Versioning.Http`, `Serilog.AspNetCore`) and of
  the SDK image in `scripts/sdk-image`, as it already did for the SDK in `global.json`. When .NET 11
  ships, its 11.0 packages no longer turn the weekly `microsoft` group into a major that waits for
  review and holds back the 10.0.x patches; moving to a new .NET stays one change you make on purpose
  (`docs/development.md`, *Bumping the SDK*; ADR-0029's amendment of 2026-10-18 records why). Their
  patch and minor updates arrive as before.
- **Dependabot's titles follow Conventional Commits**: every update block in `dependabot.yml` sets
  `commit-message` (`prefix: "build"`, `include: "scope"`), so its pull requests, and the commits
  they are squash-merged into, read `build(deps): Bump …` in every ecosystem; NuGet's were a bare
  `Bump …`.
- **Template CI boots the generated AppHost and tests each option alone** (not in generated projects):
  the legs that run the generated tests run its Aspire end-to-end test too (`RUN_ASPIRE_E2E=1` on Linux
  runners), so a renamed AppHost, the Redis wiring and the multi-tenant seed start in CI before a
  release instead of on a consumer's first push; and three new tested legs switch Redis, Serilog and
  multi-tenancy on one at a time with the UI off, so every pair of the five options meets in all four
  value combinations (idea from [jasontaylordev/CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture)).

### Removed

- **Bogus**: the package reference in `IntegrationTests` and its central version (no test used it; ADR-0009
  amendment). A generated project's `IntegrationTests` no longer references it and its `dependabot.yml`
  no longer lists it.
- `Application.UnitTests/Invoices/InvoiceTestData.cs`, replaced by `InvoiceFactory`, and
  `IdempotencyHeaderErrors` (see *Changed*).

### Fixed

- **Rider lists Trellis as its own entry.** The template no longer carries the `Aspire` classification,
  which made Rider's New Solution dialog file it as a *Type* inside the Aspire node; it now gets its own
  row under Custom Templates. `dotnet new list` shows one tag fewer; nothing in a generated project changes.
- **The Aspire E2E no longer races the test databases for ports**: run in the same `dotnet test` as the
  Testcontainers suites, the AppHost's processes bound ephemeral ports that Docker's port allocator then
  tried to use for a test database ("failed to listen on TCP socket: address already in use"). The E2E
  now sits in `AspireE2ECollection` (alone, after the parallel collections) and carries the
  `Category=AspireE2E` trait, and `ci.yml` runs it as its own step after the suites
  (`--filter-trait Category=AspireE2E --ignore-exit-code 8`, so a project that removed the reference
  flow passes the step with nothing to run). Template CI and `scripts/test-template.sh` do the same.
- **Boot-failure tests no longer flake under load**: `AuthFlowTests` and `ProblemDetailsContractTests` asserted a
  refused start with a bare `Should.Throw(() => host.CreateClient())`; about once in fifteen full-suite runs the
  test host's own race (the top-level `Program` is started on a second thread, which disposes the host when the
  start fails) surfaced `ObjectDisposedException` instead of the boot error. `BootFailure.OfAsync` boots the host,
  retries that one case on a fresh host (three attempts) and returns the real exception.
- The `.editorconfig` section that marks EF migrations as generated code, and turns every analyzer off
  there, matched any folder called `Migrations`, so a feature folder of that name (tenant or data
  migrations) compiled with no analyzer at all. It names `src/Infrastructure/*.Persistence/Migrations/`
  now.
- Two registrations of one e-mail racing each other answered 500; the loser now gets the same 409
  `Auth.EmailAlreadyInUse` as a sequential duplicate, because declared unique constraints map to a
  `Result` by their pinned name (`PK_IdempotencyKeys` → `Idempotency.Duplicate`, `UserNameIndex` →
  `Auth.EmailAlreadyInUse`; an unregistered constraint still throws) (xavierjohn/Trellis).
- An outbox processor that outlived its lease could overwrite the bookkeeping of the claimant that
  took over its row, resurrecting a processed message as pending or dead-lettered.
- A failing outbox handler held up its siblings: the handlers after it did not run until the retry.
- A change to an aggregate's children alone (a new invoice line) did not bump the root's concurrency
  token, so two such edits could both succeed against the same version.
- An endpoint returning `Task<Result<T>>` answered 200 with the `Result`'s members; the host now
  fails at boot naming `TypedResults` and `CustomResults.Problem` (xavierjohn/Trellis).
- A 257- to 320-character e-mail passed the registration validator and failed Identity's
  256-character column with a 500.
- The outcome log line was written after its use-case span had ended, so it linked to the HTTP
  request's span.
- A lower-case quote currency (`usd`) came back from the exchange-rate client as
  `ExchangeRate.UnknownCurrency`; the quote is matched case-insensitively.
- The Domain's dependency test named only the outer layers, EF Core and ASP.NET Core, so a Domain type
  could derive from FluentValidation's `AbstractValidator` or take an `ILogger`, both of which the
  SharedKernel brings along, and nothing failed. `LayerDependencyTests.Domain_depends_only_on_SharedKernel`
  is now an allow-list (`System.*`, the SharedKernel, the Domain itself), and a planted validator and
  logger prove it refuses them. The Domain csproj comment and the domain and SharedKernel rules name
  the three packages the SharedKernel carries.
- Every `/health` probe queried PostgreSQL twice: Aspire's `EnrichNpgsqlDbContext` registered its own
  check of `AppDbContext` beside the API's `database` readiness check. The enrichment's copy is switched
  off (`DisableHealthChecks`), and `HealthCheckTests` pins a single database check named `database`.

### Security
- **MessagePack pinned to 2.5.307** ahead of Aspire 13.6.1, which still pulls 2.5.302 through `Aspire.Hosting` and
  `StreamJsonRpc`: GHSA-qhrr-8q5h-9q3h (nested arrays past the buffer limit, moderate) was published on
  15 October and `NuGetAudit` rightly failed every build, the nightly run on `main` first. The pin lives in
  `Directory.Packages.props` beside the other transitive pins and moves with the Aspire Dependabot group.
