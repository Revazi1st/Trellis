# Changelog

All notable changes to the Trellis template are documented here, written for the people who
generate projects from it. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). A generated project gets its own empty
changelog; its `.trellis-template.json` records which version of this one it came from.

## [Unreleased]

If you generated a project from 0.1.0, read these before adopting this version with `docs/upgrading.md`:

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
- **`scripts/test-template.sh`** (not in generated projects): Template CI's legs on a maintainer's
  machine before the push. It reads the legs from `template-ci.yml`'s matrix, packs the template once,
  installs it into a private hive, then instantiates, builds and tests each named leg (or every leg),
  prints a table of results and exits 1 on any failure (idea from
  [jasontaylordev/CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture)'s `build/test.ps1`).
  `CONTRIBUTING.md` and the `add-toggle` skill use it instead of the manual recipe.

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
- **Dependabot leaves the next .NET major to you**: `dependabot.yml` ignores major updates of the
  packages numbered after .NET (`Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore*`,
  `Microsoft.Extensions.*`, `Npgsql.*`, `dotnet-ef`, `Asp.Versioning.Http`, `Serilog.AspNetCore`) and of
  the SDK image in `scripts/sdk-image`, as it already did for the SDK in `global.json`. When .NET 11
  ships, its 11.0 packages no longer turn the weekly `microsoft` group into a major that waits for
  review and holds back the 10.0.x patches; moving to a new .NET stays one change you make on purpose
  (`docs/development.md`, *Bumping the SDK*). Their patch and minor updates arrive as before.
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

### Security
- **MessagePack pinned to 2.5.307** ahead of Aspire 13.6.1, which still pulls 2.5.302 through `Aspire.Hosting` and
  `StreamJsonRpc`: GHSA-qhrr-8q5h-9q3h (nested arrays past the buffer limit, moderate) was published on
  15 October and `NuGetAudit` rightly failed every build, the nightly run on `main` first. The pin lives in
  `Directory.Packages.props` beside the other transitive pins and moves with the Aspire Dependabot group.
