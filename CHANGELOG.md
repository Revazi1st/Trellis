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
- **Aspire dashboard**: a "Scalar" link on the `api` resource.
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
  Windows leg in Template CI.
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
  walkthrough; the README shows the code in two excerpts. The rules gain handler rules, multi-aggregate
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

- The outcome log line no longer carries `Error.Description`, which can repeat what a user typed
  (Identity's duplicate-account text names the address) (xavierjohn/Trellis); a failed `Result`'s
  `Value` exception names the code only.
- Registration rejections never echo the e-mail: fixed sentences per Identity error code replace
  Identity's descriptions.
- SMTP credentials are refused unless STARTTLS is on, the relay's reply (which quotes the recipient)
  is dropped from the error the outbox stores, and the log line masks the recipient.
- **No `Error.Description` reaches a 5xx `detail`** (xavierjohn/Trellis): `CustomResults.Problem` works
  out the status first and writes the catalogue sentence for any status of 500 or more, keeping the
  `code`, so a third party's reply or a connection's complaint carried by a `Failure` never reaches a
  client.
- `Cache-Control: no-store` on every API response by default keeps per-user data and problem documents
  out of browser and proxy caches.

## [0.1.0] - 2026-10-08

The first release. *Added* describes what a project generated from 0.1.0 contains. *Changed*,
*Security* and *Fixed* matter if you generated a project from a pre-release snapshot of `main`; adopt
this version with `docs/upgrading.md` and read these notes first:

- **Drain the outbox before deploying.** Completion credits are now keyed on each handler's
  `[DomainEventHandlerName]` (stable names such as `invoices.paid.send-receipt`), no longer on the CLR
  type name. Rows still pending when you switch would have their handlers run once more (a second
  receipt e-mail). Let `OutboxMessages` drain to no pending rows, then deploy.
- **Idempotency v2 changes a table.** `IdempotencyKeys` gains `RequestFingerprint`, `StatusCode`,
  `ContentType`, `Body`, `Location` and `CompletedAtUtc` and a named primary key, and the shipped
  `InitialCreate` was regenerated. A deployed database needs a new migration on top
  (`docs/upgrading.md`, "Schema-affecting changes"), never the regenerated one. The stored key now
  hashes the HTTP method too, so a key reserved before the upgrade never matches a request after it:
  let in-flight keyed requests finish before switching, and the old rows age out with
  `Retention:IdempotencyKeyAge` (or delete them).
- **The wire contract changed** for API clients: problem documents, validation errors, `InvoiceDto`,
  the paging parameters and the OpenAPI document (see *Changed*). Regenerate client code from the new
  `docs/api/openapi.v1.json`.
- **Dashboards need the renamed telemetry** (see *Changed*).
- **`--use-multitenancy` adds `AspNetUsers.TenantId`** (the tenant now lives on the user): a project
  generated with the toggle from a pre-release snapshot needs that column in a migration too, and its
  existing users a tenant.

### Added

**The solution**

- A rich-DDD, Clean Architecture .NET 10 solution grouped by ring: SharedKernel, Domain and
  Application (`src/Core`), Infrastructure and Persistence as peers that never reference each other
  (`src/Infrastructure`), the API with its plumbing and public contracts and the Blazor UI
  (`src/Presentation`), Aspire service defaults (`src/Shared`), the Aspire AppHost and a migration job
  (`tools`). Architecture tests enforce the dependency flow.
- Hand-rolled CQRS without a mediator: endpoints inject handlers, a decorator pipeline runs
  Logging → Tracing → Validation → UnitOfWork → handler (queries skip the unit of work). `Result` and
  `Result<T>` with `Bind`/`Map`/`Tap`/`Ensure` and `Result.Combine`; `IUnitOfWork.CommitAsync` returns
  a `Result`, so a stale concurrency token is a 409 `Concurrency.Conflict`, never an exception.
- Domain building blocks: aggregates with factories and private EF constructors, sealed-record value
  objects (`Money`, `Currency`, `EmailAddress`) mapped as EF complex types, `IEquatable` strongly-typed
  IDs that serialize as bare GUIDs (dictionary keys included), versioned domain events
  (`[DomainEventName]`), hand-rolled specifications whose orderings end with the id.
- **Invoices**, the reference slice: create, add lines, issue (a due date that is not in the past),
  pay, void, get with a weak ETag and conditional GET, list overdue (paged); a receipt e-mail and
  business metrics from outbox-dispatched handlers; a read cache.
- **Auth**: ASP.NET Core Identity in Persistence, HS256 JWTs carrying permission claims, a permission
  catalogue (`Permissions`, `Roles.Permissions`) seeded by the migration job, single-use refresh
  tokens with replay detection (a replayed token revokes the user's whole session family), logout and
  revoke-all, account lockout (5 failures, 15 minutes, a visible 403 `Auth.AccountLockedOut`),
  `Registration:Mode` (`Closed` by default, `Open` in Development), validated auth input.
- **Exchange rates**, the reference outbound integration: a typed client behind a port returning
  `Result`, its own resilience pipeline replacing ServiceDefaults' blanket one, options validated at
  boot, a cached TCP health check reporting Degraded, Frankfurter v2 as the default provider, and an
  opt-in live smoke test (`RUN_LIVE_SMOKE_TESTS=1`).
- **Transactional outbox**: domain events are written to `OutboxMessages` in the command's
  transaction and dispatched after commit by `OutboxProcessor` (ADR-0018, ADR-0023): a
  `FOR UPDATE SKIP LOCKED` claim leases a batch, each message runs in its own scope with a dispatch
  timeout, each handler is credited under its `[DomainEventHandlerName]` with its own commit, failures
  back off 30 s → 15 min and dead-letter on the eighth (unknown types and unreadable payloads at once),
  `traceparent` and the tenant travel with the row. Validated `Outbox` options, backlog gauges, and an
  SQL re-drive runbook in `docs/deployment.md`.
- **Idempotent writes**: `Idempotency-Key` on every write endpoint; the reservation commits with the
  command, a retry with the same body is answered with the stored response (`Idempotency-Replayed:
  true`), a different body is a 422 `Idempotency.FingerprintMismatch`, a retry while the first is
  running a 409 `Idempotency.InFlight`; bodies are stored up to 64 KB per endpoint.
- **Problem details** (ADR-0024): every 4xx and 5xx is RFC 9457 with a stable `code`,
  `type` = `urn:problem-type:{code}`, a human `title`, `detail`, `instance`, `traceId` and
  `correlationId` (= `X-Correlation-ID`); a validation 400 lists `errors[]` of `{field, code,
  description}`. Unknown routes, 405, 415, malformed JSON, unparsable ids and oversized bodies are
  problem documents too, never a bodyless status or a 500.
- **OpenAPI** (ADR-0025): one document per API version with concrete paths, an `operationId` per
  endpoint class, problem responses by shape referencing a generated `ProblemResponse` schema,
  `Location`, conditional-GET members, typed status, money and version, the bearer scheme (Scalar can
  authorize). The document is committed at `docs/api/openapi.v1.json`, compared with the served one on
  every test run (`UPDATE_OPENAPI_SNAPSHOT=1` regenerates it) and diffed against the running app in CI.
- **Secure-by-default API** (ADR-0021): the versioned group requires authorization and a test fails
  any endpoint that is neither on the anonymous allowlist nor carrying a catalogued permission;
  forwarded-header trust never widens beyond loopback unless `ForwardedHeaders:*` names the proxy;
  rate limits by credential type; security headers and `no-store` on `/auth/`; 1 MB request bodies,
  32 KB headers.
- **Caching** (ADR-0026): HybridCache with an in-memory L1 (30 s) and, with `--use-redis`, a Redis L2
  (5 min); keys from the scoped `CacheKeys` service, tenant-scoped under multi-tenancy.
- **Data retention** (ADR-0027): a janitor sweeps processed and dead-lettered outbox rows, idempotency
  keys and dead refresh tokens on validated `Retention` windows.
- **Options validated at boot** everywhere (`Authentication`, `Registration`, `Email`, `ExternalApis`,
  `Outbox`, `Retention`, `Seed`): a bad value stops the host naming the key.
- **Telemetry** (ADR-0028): OpenTelemetry logs, metrics and traces over OTLP; every `Trellis.*` meter
  and source (renamed with the project) plus the Polly and EF Core meters; `service.version` and
  `deployment.environment.name` on the resource; one outcome line and one span per use case;
  `X-Correlation-ID` on every response.
- **Blazor UI** (`--include-ui`): a render-mode-agnostic component library and a Blazor Server host;
  the invoice lifecycle and registration; sessions that rotate their access token and survive
  transient refresh failures; every API call through one helper that turns failures into results;
  server validation errors under their inputs; success notifications; sign-out that revokes the
  refresh token (and a closing circuit too); a `returnUrl` that only ever goes back into the app;
  a production `/Error` page; a CSP; an accessibility pass.
- **Toggles**: `--include-ui`, `--use-redis`, `--use-serilog`, `--use-multitenancy` (the tenant comes
  from the signed `tenant` claim, every aggregate root is filtered and stamped by convention),
  `--include-ui-tests`, `--skip-restore`.

**Tooling, tests and documentation**

- Build and test without a local SDK through the SDK container (`scripts/dotnet.sh`, a digest-pinned
  base image) or the dev container; `aspire run` with a local SDK; `aspire publish` to Docker Compose
  (what it writes and what to add before production is in `docs/deployment.md`). A one-shot migration
  job that refuses the default admin password outside Development and fails loudly on a rejected seed.
- Tests: domain and application unit tests, architecture and convention tests (layers, naming,
  options, telemetry names, catch filters, query filters), Testcontainers integration tests, in-process
  API tests over the real HTTP pipeline, UI client tests, the opt-in Aspire E2E and Playwright tests.
- Generated CI: a digest-pinned gitleaks scan, format check, Release build with warnings as errors,
  tests with coverage filtered to first-party code and a gate on the Core ring, the OpenAPI contract
  diff, the SDK-container build, CodeQL (public repositories, or private ones that set
  `CODEQL_ENABLED`), grouped Dependabot updates, the docs-drift check, and with `--include-ui-tests` a
  Playwright job.
- Central package management with transitive pinning, NuGetAudit on the whole graph, and license
  ceilings enforced over the resolved package graph (MediatR, AutoMapper, FluentAssertions,
  MassTransit, Moq) (ADR-0011, ADR-0029).
- Documentation for people and agents alike: `AGENTS.md`, path-scoped `.claude/rules`, the skills
  `add-feature`, `remove-feature`, `add-integration`, `add-migration`, `new-adr` and `run-app`, two
  review agents, a docs-drift edit hook, architecture, development, deployment, upgrading and threat
  model guides, feature docs, and 29 ADRs. Project ADRs are numbered from 0100 so template upgrades
  never collide with them.
- A generated project gets its own README, an empty CHANGELOG and `.trellis-template.json` (template
  version, name and options), and none of the template's own governance files. `LICENSE` keeps the
  template's MIT notice; the README asks you to add terms for your product.

### Changed

- **Decorator pipeline**: Logging → Tracing → Validation → UnitOfWork → handler (validation used to
  run first, outside the log and span). One outcome line per use case: Information for success and
  classified expected failures, Warning for an unclassified failure, Error only for an exception.
- **Problem documents**: `type` is `urn:problem-type:{code}`, `title` is a human sentence (never
  parse it), `code`, `instance` and `correlationId` are on every problem, and each `errors[]` entry is
  `{field, code, description}` with `field` the camelCase JSON path (or `null`) and `code` the rule
  (`Validation.NotEmpty`), no longer the property name.
- **Idempotency**: the reservation is written by the command's own commit instead of before the
  handler; a retry of a completed request is answered with the stored response instead of a bare 409;
  the key's scope includes the HTTP method.
- **Contracts**: `InvoiceDto.Status` is the string enum `InvoiceStatus`, `Version` is a `long`; the
  overdue list binds `page` (1–10000) and `pageSize` (1–100) by those camelCase names and
  `PageRequest` is gone; every OpenAPI `operationId` is the endpoint's class name (`IEndpoint.Map`
  returns the `RouteHandlerBuilder`, and endpoints no longer call `.WithName`).
- **Conditional GET** parses `If-None-Match`: the strong form, a list and `*` answer 304 too.
- **Outbox**: dead letter after about 45 minutes (eight failures) instead of 7.5; a retry logs Warning
  (was Error). Metrics gain units, `trellis.outbox.event_type` replaces `event.type`, and
  `trellis.outbox.retries`, `.dispatch.duration`, `.pending` and `.oldest_pending_age` are new.
- **Telemetry names**: the invoice counters' attribute is `trellis.invoice.currency`, the request
  span's is `trellis.correlation_id`; hosts no longer list meters and sources (ServiceDefaults
  subscribes `Trellis.*`).
- **Multi-tenancy**: the tenant is read only from the signed `tenant` claim, minted from
  `AspNetUsers.TenantId`; the `X-Tenant-Id` header is ignored and a token without the claim is a 401.
  Self-registration provisions a fresh tenant; `Seed:TenantId` names the seeded admin's.
- **Registration** is closed unless `Registration:Mode` is `Open` (Development sets it).
- **E-mail**: `Email:Provider` is `Logging` (masked recipient, subject and length only, with a boot
  warning outside Development) or `None`.
- **Exchange rates** default to Frankfurter v2 (`https://api.frankfurter.dev/v2/`).
- **Domain**: `Invoice.Issue(dueDate, now)` rejects a missing or past due date
  (`Invoice.DueDateInPast`); `Void` raises `InvoiceVoidedDomainEvent`; `Money` rejects sub-cent
  amounts and can no longer go negative through subtraction.
- **Blazor sessions** end only on a 401 from the refresh endpoint; a 429, a 5xx or an unreachable API
  keeps the session and retries after 5 seconds.
- **HybridCache lifetimes** are stated: 30 s in memory, 5 min in Redis.
- The seeded admin is `admin@example.com` (password unchanged), and every generated project gets its
  own Development JWT signing key.

### Security

- Endpoints require authorization by default, `RequirePermission` rejects an unknown permission at
  startup, and an anonymous endpoint must be on a tested allowlist.
- Forwarded headers are trusted only from loopback or the configured proxies, even when
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (a startup warning names the settings).
- HS256 is the only accepted signing algorithm; 401s carry `WWW-Authenticate`; the API refuses to
  start without a signing key of at least 32 characters.
- Login spends the same hashing work for an unknown e-mail as for a wrong password, logs security
  events with masked e-mails, and lockout throttles per account.
- Security headers on every API response, a CSP and frame protection on the web host, `no-store` on
  auth responses, request size limits.
- Problem documents never carry exception text; a production 500 is opaque.
- The tenant can no longer be chosen by a request header (`--use-multitenancy`), and the read cache is
  tenant-scoped.
- A blank `Idempotency-Key` is a 400; keys are scoped per user, method and route and hashed.
- Signing out of the web UI revokes the refresh token at the API, and the sign-in page's `returnUrl`
  cannot redirect outside the app.
- The migration job refuses to seed the default admin password outside Development, and demo data
  seeds in Development only.

### Fixed

- An idempotent retry after a client disconnect could apply the command twice (a cancelled cache
  eviction released the key after the commit).
- `--use-serilog` projects exported no application logs to OpenTelemetry.
- `GET /invoices/{id}` answered 404 for the caller's own invoice under `--use-multitenancy` (the
  cache factory ran without the request's tenant).
- `aspire run` pointed every resource at the Compose database instead of the provisioned one.
- `--use-multitenancy` projects could not start (no `ITenantContext` outside the API host).
- Blazor sessions died after 15 minutes (the refresh token was never used), and the UI's POSTs could
  be duplicated by retries (no idempotency key was sent).
- Rate limiting never partitioned per user and collapsed behind a proxy.
- Refresh-token rotation was not atomic; registration could leave an account without its role.
- Outbound calls to the reference integration escaped the `Result` boundary on a timeout or an open
  circuit, and the client's retries multiplied with ServiceDefaults' blanket handler.
- A failed bookkeeping save in the outbox processor stranded the leases of the rest of its batch.
- Paging could repeat or skip rows with equal sort keys; non-UTC `DateTimeOffset` values were
  rejected by PostgreSQL.
- A bad `Seed` option crashed the migration job (exit 134) instead of failing it with the key named.
- The generated UI was unstyled in every project not named after the template, and the Blazor
  invoice list showed only its first page.
- Projects named in kebab case produced a solution that did not load; such names now get a warning
  and a consistent tree.

[Unreleased]: https://github.com/Revazi1st/Trellis/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Revazi1st/Trellis/releases/tag/v0.1.0
