# ADR-0028: Use-case telemetry: one outcome line, one span, prefixed names

- **Status**: accepted
- **Date**: 2026-10-08
- **Amended**: 2026-10-11 — the span wraps the line, descriptions leave the line, stable event ids, final span attribute names, a validation-failure counter (see *Amendment* below)
- **Amended**: 2026-10-11 — `ErrorType.PreconditionFailed` is a classified expected failure at Information; `ErrorType.Unavailable` stays at Warning (see *Second amendment* below)
- **Amended**: 2026-10-18 — the caller's user id on every request signal, a log category per use case, an opt-in slow-use-case Warning (see *Third amendment* below)
- **Prior art**: [CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture) by Jason Taylor (MIT): logging each use case under its own request type's category (`ILogger<TRequest>`), the acting user's id on every request's log line, and a slow use case surfaced at Warning past a threshold (`LoggingBehaviour`, `PerformanceBehaviour`) (the 2026-10-18 amendment)

## Context

The decorator pipeline (ADR-0003) is the one place every use case passes through, so it is where
telemetry belongs. Before this decision it logged unevenly: every expected failure at Warning (an
alert channel full of 404s), validation failures not at all (validation ran outside the logging
decorator), a crashing commit with no use-case line, and no span at all. Meters and activity sources
had to be registered by hand in every host, so a meter added in a feature was created, incremented
and never exported. Instrument and attribute names followed no rule, which matters because they are
a contract with every dashboard and alert built on them.

## Decision

- **One outcome line per invocation**, written by `LoggingDecorators` (outermost) inside a
  `UseCase {UseCase}` log scope that everything below it inherits: Information for success and for a
  classified expected failure (Validation, NotFound, Conflict, Unauthorized, Forbidden; a validation
  line lists its `field:code` pairs), Warning for an unclassified `ErrorType.Failure`, Error only for
  an exception that ends the use case, Information for the caller's own cancellation. Each line carries
  the elapsed milliseconds. Handlers do not log their own outcomes.
  *(Amended 2026-10-11: `PreconditionFailed` is in the Information set, `Unavailable` logs at Warning; see the second amendment.)*
- **One span per invocation**, started by `TracingDecorators` on the `Trellis.UseCases` source and named
  after the use case, tagged with the outcome (`success`, `failure`, `cancelled`, `exception`), the
  error's code and type on a failure, and the exception itself (`AddException`, status Error) on a crash.
  The three tags ship as `usecase.outcome`, `usecase.error_code` and `usecase.error_type`; they are on
  `TelemetryNamingTests`' pending-rename list, to become `trellis.usecase.outcome`,
  `trellis.usecase.error_code` and `error.type`.
- **Subscription by prefix.** ServiceDefaults subscribes every meter and activity source whose name
  matches `Trellis.*` (the project's name after `dotnet new`), plus the `Polly` and
  `Microsoft.EntityFrameworkCore` meters, in every host. A new meter needs no host registration.
- **Names are rules, enforced.** Meters and sources `Trellis.<Area>`; instruments
  `trellis.<area>.<name>` with a declared unit; a custom attribute `trellis.<area>.<name>` unless an
  OpenTelemetry semantic-convention name carries exactly its meaning (`error.type`,
  `deployment.environment.name`, `service.version`, the `exception.*` set). Names are `const string`s
  so a source scan can resolve them.
- **Who emits it**: `service.name` from `OTEL_SERVICE_NAME` (Aspire sets it) or the host's name,
  `service.version` the informational version, `deployment.environment.name` the ASP.NET Core
  environment; a key in `OTEL_RESOURCE_ATTRIBUTES` wins. Requests carry `trellis.correlation_id`, the
  same value as the `X-Correlation-ID` header and the problem body's `correlationId` (ADR-0024).

## Consequences

- An alert on Warning or Error means something unexpected happened; the volume of expected failures
  is visible on the spans and in Information logs instead.
- A dashboard can rely on the names: renaming an instrument or attribute is a breaking change for the
  people operating the system and is called out in the changelog.
- Fitness: `UseCaseOutcomeTests` (the level per outcome class, one line per invocation, the span's
  tags and exception), `DecoratorPipelineTests` (the exact chain for every use case),
  `TelemetryNamingTests` (prefixes, units, resolvable names, the pending renames still pending),
  `ServiceDefaultsTests` (a prefixed meter and source are exported and an unprefixed one is not; the resource and the `OTEL_*` overrides).

## Amendment (2026-10-11): the span wraps the line; the line names the error, not its text; final names

- **The span wraps the line.** `TracingDecorators` now runs outside `LoggingDecorators` (ADR-0003's
  second amendment), so the outcome line is written while the use-case span is current: an OTLP log
  record carries that span's trace and span ids, and a trace view shows the line under its use case.
- **No descriptions by default.** A failure line reads `failed in {ElapsedMilliseconds}ms with
  {ErrorType} ({ErrorCode})`; a `ValidationError` adds its `field:code` pairs. `Error.Description` is
  left out: it can carry what a user typed or a library wrote (Identity's duplicate-account text names
  the address), and the line is written at Information for every request. The type and code identify
  the failure; the response carries the text. `UseCaseLoggingOptions.IncludeErrorDetail` (default
  false) appends the description; a host binds it from `Telemetry:IncludeErrorDetail`, and only
  Development turns it on.
- **Stable event ids.** The outcome lines are `[LoggerMessage]` methods in the use-case range 1001–1099,
  beside the security events' 11xx and the e-mail sender's 12xx: 1001 succeeded, 1002 failed, 1003
  failed validation (with the pairs), 1004 failed with the description (written instead of 1002 while
  the opt-in is on), 1005 cancelled by the caller, 1006 threw. An alert or a query matches the id, not
  the message text. Renumbering one is a breaking change, like renaming an instrument.
- **Final span attribute names.** The three tags are `trellis.usecase.outcome`,
  `trellis.usecase.error_code` and `trellis.usecase.error_type`. The third is not `error.type`, as the
  decision above planned: backends count a span carrying `error.type` as an error, so every 404 and
  rejected form would read as a fault. `error.type` is set only when the use case throws, to the
  exception's full type name, beside status Error (the exception's message as its description) and the
  exception event. `TelemetryNamingTests`' pending-rename list is empty.
- **`trellis.validation.failures`** (`{failure}`, on the `Trellis.UseCases` meter) counts every failure of
  a Validation-typed result, each entry of a `ValidationError` or the single error a handler returned,
  tagged `trellis.usecase.name` and `trellis.validation.code` (never the field or the value). It is
  counted at the outcome site, in `TracingDecorators`, so a domain rule's code (`Invoice.DueDateInPast`)
  counts beside FluentValidation's: the rules that reject the most requests, by use case.
- Fitness: `UseCaseOutcomeTests` asserts the event id per outcome, that a failure line omits the
  description unless the opt-in is set, that the line carries the use-case span's id, the span
  attribute names as literals, `error.type` on a crash only, and the counter for a FluentValidation and
  a domain code.

## Second amendment (2026-10-11): the levels of `PreconditionFailed` and `Unavailable`

ADR-0004's amendment added `ErrorType.Unavailable` (503) and `ErrorType.PreconditionFailed` (412) after
the decision above fixed the Information set, and `LoggingDecorators` logged both at Warning as
unclassified types. `PreconditionFailed` is now a classified expected failure: a stale `If-Match` is the
concurrency contract working, as routine as a 404, and a page that retries with the fresh ETag would
otherwise fill the Warning channel with its own users' edits. It logs at Information. `Unavailable` stays
at Warning beside an unclassified `Failure`: the request did nothing wrong, but a dependency being down
is what the Warning channel is for, and the first line written after the retries, the circuit breaker and
the limiter have given up should reach whoever is on call. `UseCaseOutcomeTests.Every_error_type_logs_one_line_at_its_level`
pins both rows; `.claude/rules/application.md`'s pipeline line names both levels.

## Third amendment (2026-10-18): the caller's user id, a log category per use case, a slow-use-case Warning

- **The caller's user id on every request signal.** `UserTelemetryMiddleware` (Api.Common) runs right
  after authentication and, for a signed-in caller, opens a `CallerUserId` log scope and tags the request
  span `enduser.id` with the caller's user id. The outcome line and whatever a handler logs then name the
  account; log records carry the scope because ServiceDefaults sets `IncludeScopes`. A 403 from
  authorization is named by its request span alone at the shipped levels: ASP.NET Core writes the failure
  at Information under `Microsoft.AspNetCore`, which `appsettings.json` holds at Warning, and nothing
  else logs it. The scope is not called `UserId`: the security events already name the account they are
  about `{UserId}`, which need not be the caller, and one record cannot hold both under one key (an OTLP
  exporter writes both attributes, Serilog keeps the event's). The id only, never the name or the e-mail,
  for the reason descriptions left the line: it is written for every request. `enduser.id` is the
  OpenTelemetry semantic convention for the end user an operation runs for (deprecated in semantic
  conventions 1.26 and restored in 1.31 for exactly that); `user.id` belongs to the generic user
  namespace, whose own example is an operating-system account, so it does not carry exactly this meaning.
  Fitness: `UserTelemetryTests` (ApiTests: an authenticated request's outcome record carries the id in
  its scope and its request span carries `enduser.id`; an anonymous request's carry neither),
  `TelemetryNamingTests` (`enduser.id` on the semantic-convention list; no message template names
  `CallerUserId`).
- **A log category per use case.** The three `LoggingDecorators` take `ILogger<TCommand>` or
  `ILogger<TQuery>` instead of a logger typed by the decorator, so each outcome line is logged under its
  use case's own type (`Trellis.Application.Features.Invoices.ListInvoices.ListInvoicesQuery`) and a
  namespace prefix in `Logging:LogLevel` (or `Serilog:MinimumLevel:Override`) sets one feature's level.
  Before, every use case shared the decorator's category, so an operator could quiet all commands or all
  queries and nothing finer. A feature set to Warning drops its success and expected-failure lines and
  keeps its Warning and Error lines and its spans; the decision above still holds, since the levels
  themselves do not change. The loggers stay injected rather than built from an `ILoggerFactory`, so a
  container that registers the open generic `ILogger<>` sees every line. The category is no longer
  something to match an outcome line by: match the event ids (1001–1099), as the tests now do.
  Fitness: `UseCaseOutcomeTests.Each_use_case_logs_under_its_own_message_type` (all three decorator
  shapes), `SerilogExportTests` (the category survives the Serilog bridge).
- **A slow use case at Warning, when an operator asks.** `Telemetry:SlowUseCaseThreshold` (unset by
  default, 1 ms to 1 hour, validated at boot with the rest of `UseCaseLoggingOptions`) makes a use case
  that returns a result after at least that long write event 1007 `UseCaseSlow` at Warning after its
  outcome line, carrying the use case's name and elapsed milliseconds and nothing else. The consequence
  above stays true: a Warning still means something unexpected, because nothing is written until someone
  says what slow is for this system. A use case that throws or is cancelled already has its own line with
  its elapsed time, so it gets no second one. There is no duration histogram: ASP.NET Core's
  `http.server.request.duration` by route already charts every HTTP use case; one is worth adding when a
  use case runs outside HTTP (the scheduled-job recipe in `docs/architecture.md`). Fitness:
  `UseCaseOutcomeTests` (past the threshold, a success and a failure each add one Warning under the use
  case's category; under it or without one, none), `OptionsConventionTests` (out of range stops the boot),
  `UseCaseLoggingOptionsTests` (bound from the section, off by default).
