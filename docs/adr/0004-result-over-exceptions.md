# ADR-0004: Result pattern over exceptions for expected failures

- **Status**: accepted — the HTTP mapping paragraph is superseded by ADR-0024 (problem shape v2); the Result decision stands
- **Date**: 2026-07-14
- **Amended**: 2026-10-08 — the unit of work sits inside the Result boundary; the HTTP shape moved to ADR-0024 (see *Amendment* below)

## Context

Operations fail in expected ways (validation, not-found, conflict, forbidden). Modelling those as exceptions conflates control flow with faults, is costly, and hides the failure contract from the signature.

## Decision

Hand-roll a small `Result`/`Result<T>` + `Error(Code, Description, ErrorType)` in `SharedKernel.Results` (no ErrorOr/FluentResults dependency). Domain methods, handlers, and value-object factories return `Result`; **exceptions are reserved for the genuinely unexpected**. `ErrorType` ∈ { Validation, NotFound, Conflict, Unauthorized, Forbidden, Failure }.

`Error` is the only "reserved keyword" name we allow (CA1716 suppressed) because it is the Result vocabulary. Feature-specific errors live in `{Aggregate}Errors` static classes. `ValidationError` aggregates per-field failures.

At the HTTP boundary, `Api.Common/Results/CustomResults.Problem(Error)` maps `ErrorType` → RFC 9457 ProblemDetails status (Validation→400 with an `errors[]` extension, NotFound→404, Conflict→409, Unauthorized→401, Forbidden→403, Failure→500). A global `IExceptionHandler` handles only truly unhandled faults.

## Consequences

- Failure is part of the type — callers must `Match`/branch; no silent swallow.
  *(Amended 2026-10-11: the build enforces this only in part. CA1806, configured in `.editorconfig`, fails a
  Release build in `src/` or `tools/` that drops the value of any Domain member or `Result` combinator; it
  matches by documentation ID, not by type, and cannot see a dropped `await` of a `Task<Result>`. A
  type-aware analyzer would close both gaps; it needs a new project and the Roslyn packages, so it waits for
  approval.)*
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
