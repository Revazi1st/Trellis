# ADR-0004: Result pattern over exceptions for expected failures

- **Status**: accepted — the HTTP mapping paragraph is superseded by ADR-0024 (problem shape v2); the Result decision stands
- **Date**: 2026-07-14
- **Amended**: 2026-10-08 — the unit of work sits inside the Result boundary; the HTTP shape moved to ADR-0024 (see *Amendment* below)
- **Amended**: 2026-10-11 — `ErrorType.Unavailable` (503) for a dependency that is down for now, `ErrorType.PreconditionFailed` (412) for a version the caller expected (see *Amendment (2026-10-11)* below)
- **Amended**: 2026-10-11 — a discarded `Result` fails the Release build: RSLT001, the type-aware analyzer of ADR-0030, replaced the CA1806 stand-in of the same day (see *Consequences*)

## Context

Operations fail in expected ways (validation, not-found, conflict, forbidden). Modelling those as exceptions conflates control flow with faults, is costly, and hides the failure contract from the signature.

## Decision

Hand-roll a small `Result`/`Result<T>` + `Error(Code, Description, ErrorType)` in `SharedKernel.Results` (no ErrorOr/FluentResults dependency). Domain methods, handlers, and value-object factories return `Result`; **exceptions are reserved for the genuinely unexpected**. `ErrorType` ∈ { Validation, NotFound, Conflict, Unauthorized, Forbidden, Failure }.
*(Amended 2026-10-11: `ErrorType` also has `Unavailable` and `PreconditionFailed`; see below.)*

`Error` is the only "reserved keyword" name we allow (CA1716 suppressed) because it is the Result vocabulary. Feature-specific errors live in `{Aggregate}Errors` static classes. `ValidationError` aggregates per-field failures.

At the HTTP boundary, `Api.Common/Results/CustomResults.Problem(Error)` maps `ErrorType` → RFC 9457 ProblemDetails status (Validation→400 with an `errors[]` extension, NotFound→404, Conflict→409, Unauthorized→401, Forbidden→403, Failure→500). A global `IExceptionHandler` handles only truly unhandled faults.

## Consequences

- Failure is part of the type — callers must `Match`/branch; no silent swallow.
  *(Amended 2026-10-11: the build enforces this. RSLT001 (ADR-0030), an analyzer every project loads, fails
  a Release build on a statement that drops a `Result`, a `Result<T>` or an `IdentityResult`, awaited or not,
  in every project, the tests included; `_ = …` with a one-line reason drops one on purpose. It replaced the
  CA1806 stand-in, which matched by name and could not see through `await`.)*
- One translation seam (`CustomResults.Problem`) owns Result→HTTP; endpoints stay `result.IsSuccess ? TypedResults.X : CustomResults.Problem`.
- No exceptions-as-flow cost; the global handler surface is small.

## Amendment (2026-10-08): the unit of work sits inside the Result boundary; the HTTP shape is ADR-0024's

Two corrections to the text above, recorded here because ADRs are immutable (ADR-0001):

- **The unit of work sits inside the Result boundary.** The decision originally stopped at the handler:
  a stale concurrency token was an EF exception caught by a dedicated exception handler on its way out.
  That put one expected failure on the exception path this ADR forbids. `IUnitOfWork.CommitAsync` now
  returns `Result`; a stale `xmin` is `UnitOfWorkErrors.ConcurrencyConflict` (`Concurrency.Conflict`), the
  UnitOfWork decorator propagates it like any other failure, and the endpoint maps it through
  `CustomResults.Problem` (409). There is no concurrency exception handler, and nothing in Presentation
  catches EF exceptions. The one `IExceptionHandler` left for faults is the opaque 500; a request the
  framework could not bind (malformed JSON, an unparsable id) is a `BadHttpRequestException` answered as
  a 400 problem by its own handler — still not an expected *domain* failure, so still not a Result.
- **The HTTP mapping paragraph is superseded by ADR-0024.** `CustomResults.Problem(Error)` remains the
  single Result → HTTP seam and the `ErrorType` → status table is unchanged, but the document it writes
  — human `title`, `code`/`type`/`instance`/`correlationId` on every problem, `errors[]` of
  `{field, code, description}` on every Result-produced 400 — is specified there, together with the
  catalogue and the single writer for the non-Result paths.

## Amendment (2026-10-11): a dependency that is down for now is `Unavailable`, not `Failure`

The six types left one question unanswerable from the status: is this our bug, or is something we
depend on down? The reference integration's outage (`ExchangeRate.Unavailable`) was a deliberate
`Failure`, so a gateway, a dashboard or a client saw the same 500 for a third party timing out as for a
defect. `ErrorType` gains **`Unavailable`**, with an `Error.Unavailable(code, description)` factory:
something this operation depends on is unreachable, too slow, or refused by our own circuit breaker,
and the same request may succeed later. `CustomResults.Problem` maps it to **503** with the title
"Service unavailable" and a fixed `detail` that only says to retry later (ADR-0024's second
amendment); the outcome line logs it at Warning, the level every unclassified type gets (ADR-0028).

`Unavailable` is not a softer `Failure`. A dependency that answered and **refused** us (our
credentials, our plan, a URL it does not serve) or that answered something unreadable stays a
`Failure`: no wait cures our configuration, and a 503 would tell every client to retry a permanent
fault. The reference adapter draws that line (ADR-0020, amendment of 2026-10-11). The
`ErrorType` → status table is now Validation → 400, NotFound → 404, Conflict → 409, Unauthorized →
401, Forbidden → 403, PreconditionFailed → 412, Unavailable → 503, Failure → 500. `Retry-After` on the 503 is deferred: by the
time an adapter reports `Unavailable` its retry budget is spent, so there is rarely an honest value to
send; an endpoint that has one (an open circuit's break) sets the header before returning
`CustomResults.Problem`, the way the idempotency filter's 409 does.

**`PreconditionFailed` for a version the caller expected.** An optional `If-Match` on the invoice writes
(ADR-0025, amendment of 2026-10-11) needs an outcome the six types could not express: the caller named
the version it read, and the aggregate has moved on. That is not a `Conflict` (409 already means "the
state machine forbids this", and the commit's own `Concurrency.Conflict`), and a status computed from a
request header at the seam would need `CustomResults.Problem` to see the request, which it deliberately
does not. So the handler, which compares the version, returns `Error.PreconditionFailed(code, description)`
and the seam maps it to **412** with the title "Precondition failed". Like `Unavailable`, the outcome line
logs it at Warning until the logging decorator classifies it among the expected 4xx outcomes.
