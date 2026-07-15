# ADR-0004: Result pattern over exceptions for expected failures

- **Status**: accepted
- **Date**: 2026-07-14

## Context

Operations fail in expected ways (validation, not-found, conflict, forbidden). Modelling those as exceptions conflates control flow with faults, is costly, and hides the failure contract from the signature.

## Decision

Hand-roll a small `Result`/`Result<T>` + `Error(Code, Description, ErrorType)` in `SharedKernel.Results` (no ErrorOr/FluentResults dependency). Domain methods, handlers, and value-object factories return `Result`; **exceptions are reserved for the genuinely unexpected**. `ErrorType` ∈ { Validation, NotFound, Conflict, Unauthorized, Forbidden, Failure }.

`Error` is the only "reserved keyword" name we allow (CA1716 suppressed) because it is the Result vocabulary. Feature-specific errors live in `{Aggregate}Errors` static classes. `ValidationError` aggregates per-field failures.

At the HTTP boundary, `Api.Common/Results/CustomResults.Problem(Error)` maps `ErrorType` → RFC 9457 ProblemDetails status (Validation→400 with an `errors[]` extension, NotFound→404, Conflict→409, Unauthorized→401, Forbidden→403, Failure→500). A global `IExceptionHandler` handles only truly unhandled faults.

## Consequences

- Failure is part of the type — callers must `Match`/branch; no silent swallow.
- One translation seam (`CustomResults.Problem`) owns Result→HTTP; endpoints stay `result.IsSuccess ? TypedResults.X : CustomResults.Problem`.
- No exceptions-as-flow cost; the global handler surface is small.
