# ADR-0028: Use-case telemetry: one outcome line, one span, prefixed names

- **Status**: accepted
- **Date**: 2026-10-08

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
