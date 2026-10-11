# ADR-0024: Problem details v2 — one shape, one writer, a stable code on every problem

- **Status**: accepted
- **Date**: 2026-10-08
- **Supersedes**: the "At the HTTP boundary…" paragraph of ADR-0004 (the Result decision itself stands).
- **Refines**: ADR-0021 (the 401/403/429 writers it introduced now go through the problem-details service).
- **Amended**: 2026-10-11 — `Result.Combine` merges only validation failures; a handler names the request field of a value-object or aggregate-rule error (`AtField`); a `Result` never reaches JSON (see *Amendment* below)

## Context

The first error contract mapped `Result` failures to RFC 9457 at one seam (`CustomResults.Problem`,
ADR-0004) but left the document itself half-specified, and the audit found six consequences:

1. `title` was the machine code (`"Invoice.NotFound"`) and `type` ASP.NET's generic RFC 9110 link; there
   was no `code` member, so clients branched on `title` or on `status` alone.
2. A 400 carried `errors: [{code, description}]` where `code` was FluentValidation's `PropertyName`
   (`"CustomerName"`) for validator failures and a real code (`"EmailAddress.Invalid"`) for domain errors;
   a *single* domain validation error had no `errors[]` at all.
3. 401, 403 and 429 were hand-written with `WriteAsJsonAsync`: no `type`, no `traceId`, and the
   `CustomizeProblemDetails` hook never ran for them. An unknown route's 404, a 405 and a 415 had no body.
4. Malformed JSON, a missing body or `/invoices/not-a-guid` surfaced as `BadHttpRequestException`,
   which the catch-all handler turned into a **500 "Server failure"** in Development (where every
   in-process test runs) and the framework into a **bodyless 400** everywhere else.
5. Three title conventions coexisted; the idempotency filter built its own `TypedResults.Problem` twice.
6. Nothing carried the correlation id, so a support engineer holding a problem body could not find the
   log lines without the response headers.

## Decision

### The wire contract

Every response with status ≥ 400 is `application/problem+json` with this shape (★ = added here):

```json
{
  "type": "urn:problem-type:Validation.General",   ★ always ours, never the RFC 9110 link
  "title": "Validation failed",                    ★ human, fixed per error class
  "status": 400,
  "detail": "One or more validation errors occurred.",
  "instance": "/api/v1/invoices",                  ★ the request path
  "code": "Validation.General",                    ★ machine discriminator == Error.Code
  "correlationId": "01JB8Z5M9K-trace.id:42",       ★ == the X-Correlation-ID response header
  "traceId": "00-dc43cf54…-01",                    (ASP.NET's defaults)
  "errors": [                                      ★ on every 400 produced from a Result; absent otherwise
    { "field": "customerName", "code": "Validation.NotEmpty", "description": "'Customer Name' must not be empty." },
    { "field": null,           "code": "Invoice.NoLines",     "description": "An invoice needs at least one line before it can be issued." }
  ]
}
```

1. **`code`** follows `Area.Problem` — PascalCase both sides, exactly one dot (`Invoice.NotFound`,
   `Auth.InvalidCredentials`, `Validation.NotEmpty`, `Request.Malformed`). Codes are what clients
   switch on; they never change meaning once shipped. A problem **never** omits `code`.
2. **`type`** is `"urn:problem-type:" + code`: one spelling of each identifier across `code`, `type`,
   logs and docs, and a URN because nothing is served at it (RFC 9457 §4 allows exactly that). The prefix
   is one constant (`ProblemTypes.Prefix`) a product changes to a documentation URL if it publishes one.
3. **`title`** is a short human phrase per error class, no trailing period: Validation → "Validation
   failed", NotFound → "Not found", Conflict → "Conflict", Unauthorized → "Unauthorized", Forbidden →
   "Forbidden", Failure → "Server failure"; the non-Result rows use the catalogue titles below. Titles are
   for people, not for branching.
4. **`detail`** is `Error.Description` or the catalogue sentence. Exception messages never reach `detail`
   except the framework's own `BadHttpRequestException.Message`, which names a parameter or a limit, not
   a stack. The 500's detail is always "An unexpected error occurred."
5. **`errors[]`** appears on every 400 produced from a `Result` (`CustomResults.Problem` with
   `ErrorType.Validation`), never elsewhere:
   - `field` is the **JSON path of the offending request member** (`customerName`, `lines[0].amount`:
     camelCase per segment, computed at the API seam from FluentValidation's `PropertyName`), or `null`
     when the error is about the request as a whole (every domain value-object error). *(Superseded by
     the 2026-10-11 amendment below: a handler names the field of a value-object or aggregate-rule error
     with `AtField`; `null` remains for an error about the request as a whole, such as `Invoice.NoLines`.)*
   - `code` per entry: a FluentValidation failure is `Validation.` + the validator name without its
     `Validator` suffix (`Validation.NotEmpty`, `Validation.MaximumLength`, `Validation.Email`); a rule with
     `.WithErrorCode("Invoice.DueDateInPast")` passes its code through; a failure with no code is
     `Validation.Invalid`. A domain error keeps its own code.
   - A 400 from a **single** plain validation error (`Invoice.NoLines`) still carries
     `errors: [{ field: null, code: "Invoice.NoLines", … }]` with top-level `code: "Invoice.NoLines"`; a
     `ValidationError` carries top-level `code: "Validation.General"` and the list. Clients can therefore
     always render `errors[]` on a 400 and never special-case.
6. **`correlationId`** equals the `X-Correlation-ID` response header — the inbound value when the caller
   sent a safe one, else the request's W3C trace id. `CorrelationIdMiddleware` keeps the id in
   `HttpContext.Items` for the enrichment and re-asserts the header when the response starts, because the
   exception-handler middleware clears the response before it writes the 500.

Rejected alternative: ASP.NET's `HttpValidationProblemDetails` (`errors: { "customerName": ["…"] }`).
It is what `TypedResults.ValidationProblem` emits, but it drops the per-entry `code` a localising client
needs and cannot express whole-request errors; the array keeps the shipped member name and is stated
explicitly in the OpenAPI document (ADR-0025), so generators do not guess.

### In SharedKernel

`FieldError(Field, Code, Description) : Error` is what `ValidationDecorators` emits per FluentValidation
failure (it replaces the internal `FieldValidationFailure` of the pipeline-v2 pass); domain errors stay
plain `Error`s; `ValidationError` aggregates both and `Result.Combine` flattens nested ones unchanged.
The outcome log line's `field:code` pairs read the same record. *(Refined by the 2026-10-11 amendment
below: `Combine` merges only when every failure is a validation error.)*

### One writer, one catalogue (Api.Common)

- `CustomResults.Problem(Error)` is the only Result → HTTP translation (unchanged signature).
- `ProblemCatalog` holds every situation that does **not** come from a Result, one row each:
  `Auth.Unauthenticated` (401), `Auth.Forbidden` (403), `RateLimit.Exceeded` (429), `Request.Malformed`
  (400), `Request.TooLarge` (413), `Request.UnsupportedMediaType` (415), `Server.Failure` (500). The
  catalogue does **not** carry `Concurrency.Conflict`: the unit of work returns it as a Result
  (`UnitOfWorkErrors.ConcurrencyConflict`) and the Result path maps it. The idempotency filter's
  outcomes are `IdempotencyErrors` (`Idempotency.InvalidKey` 400, `Idempotency.InFlight` and
  `Idempotency.Duplicate` 409 — the type lives in `Trellis.Application.Abstractions` since the ADR-0018
  amendment, because Persistence produces `Duplicate`) written through `CustomResults`, like any
  endpoint's; the 422 `Idempotency.FingerprintMismatch` has no `ErrorType`, so it is a `ProblemDescriptor`
  written through `WriteProblemAsync`.
- `HttpContext.WriteProblemAsync(ProblemDescriptor)` is how a non-Result path writes: through
  `IProblemDetailsService`. The JWT challenge (keeping `WWW-Authenticate`), the forbidden callback, the
  rate limiter's rejection (keeping `Retry-After`) and both exception handlers use it. Nothing writes a
  problem with `TypedResults.Problem` or `WriteAsJsonAsync` directly.
- `AddProblemDetails(CustomizeProblemDetails)` in `AddApiCommon` is the single enrichment point: it fills
  `instance`, overwrites `type` with the URN, derives `Http.{ReasonPhrase}` (and the catalogue's
  413/415 rows) for a problem nobody authored, and adds `correlationId`.
- `app.UseStatusCodePages()` gives every response that would leave with a status and no body a problem
  document: an unknown route's 404 (`Http.NotFound`), a 405 (`Http.MethodNotAllowed`, `Allow` kept), the
  request-timeout 504, Kestrel's 413. Responses that already wrote a body are untouched.
- `RouteHandlerOptions.ThrowOnBadRequest = true` in every environment, and
  `BadHttpRequestExceptionHandler` (registered before the catch-all) answers the exception's own status
  as a problem and logs at **Information**: a malformed request is a client error, not an incident. The
  exception-handler middleware's default already suppresses its Error log and `error.type` metric for an
  exception an `IExceptionHandler` handled, so no diagnostics callback is configured.

### The client mirror

`Api.Contracts.Common.ProblemResponse` and `FieldError` are the wire shape as records: tolerant readers
(every member nullable) that the UI deserialises and the OpenAPI document generates its problem schema
from (ADR-0025). `Trellis.Api.Common` therefore references `Trellis.Api.Contracts` — a BCL-only leaf, so
the dependency still points inward: `Api → Api.Common → Api.Contracts ← UI.Common`.
`LayerDependencyTests` pins the direction (Contracts never references Common).

### What consumers may rely on

- The status, `code`, `type == "urn:problem-type:" + code`, `instance`, `traceId`, `correlationId`, and —
  on a 400 produced from a Result — `errors[].field` (a JSON path or null), `errors[].code`.
- `detail` and `errors[].description` are safe to show to a user on a 4xx; a 5xx carries nothing
  specific by design.
- Not the wording of `title` or `detail`: both are for people and may be rephrased or localised without a
  version bump. Branch on `code`.

`ProblemDetailsContractTests` proves every row of this contract through the real pipeline, one status at
a time, by deserialising each response into `ProblemResponse`.

## Consequences

- Adding an error means adding a code to an `*Errors` class — nothing changes in the API; a new
  non-Result situation gets a `ProblemCatalog` row and is written with `WriteProblemAsync`.
- `FieldError.Field` is a request JSON path on the wire, so a Blazor form can feed `errors[]` straight
  into a `ValidationMessageStore` keyed by field (the UI follow-up), and an API client can highlight the
  offending input without parsing messages.
- A support engineer holding either the body or the headers of a failed call has the correlation id and
  the trace id; the two always agree.
- Malformed input no longer produces Error-level log lines or a 500 count, and the shipped behaviour is
  identical in Development and Production — the in-process tests prove what production does.
- The `type` URN is not dereferenceable. A product that wants documentation links changes
  `ProblemTypes.Prefix` once; every consumer that branched on `code` is unaffected.

## Amendment (2026-10-11): `Result.Combine` merges only validation failures; value-object errors name their field; a `Result` never reaches JSON

**`Combine` is type-aware.** As first written, `Result.Combine` flattened any two failures into one `Validation.General`, so two
NotFounds, or a NotFound beside a Failure, answered 400 and logged at Information. It now merges into a
`ValidationError` only when every failure is `ErrorType.Validation` (nested ones still flattened);
otherwise it returns one error unmerged: the first `Failure`, else the first non-validation failure.
The validation errors beside it are dropped, because the 404 or the 500 is the answer that matters. A
single failure is returned as it is. `Combine<T1, T2>` and `Combine<T1, T2, T3>` apply the same rule
and keep the values as a tuple, which the deconstructing `Bind`/`Map` overloads unpack, so
`CreateInvoiceCommandHandler` is one chain. The wire contract above is unchanged.

**Value-object and aggregate-rule errors name the request field.** Rule 5 made every domain
value-object error a whole-request error, so an API client or the Blazor form could not put
"The email address is not in a valid format." under the e-mail input when `"a@b"` passed every shape
check and only `EmailAddress.Create` rejected it. The Domain stays transport-free: factories and
aggregate methods still return plain `Error`s. The handler, which knows which request member it passed,
attributes the failure with `Result.AtField(member)` (in `ResultExtensions`):
`EmailAddress.Create(command.CustomerEmail).AtField(nameof(command.CustomerEmail))`. `AtField` rewraps
a plain `ErrorType.Validation` error as `FieldError(member, code, description)`. It passes through every
other type, an existing `FieldError` and an aggregated `ValidationError`, so each value object is
attributed before `Combine`. The code-selective overload, `AtField(member, params Error[] only)`, serves a
value object spanning two inputs or an aggregate method with several rules: `Invoice.Issue` attributes
`Invoice.DueDateInPast` to `dueDate` while `Invoice.NoLines` stays `field: null`. The shape of `errors[]`
is unchanged and so is every `code`; only which entries carry a path changed. A client that read
`field: null` as "a domain error" branches on `code` instead, as "What consumers may rely on" already says.
`ProblemDetailsContractTests` pins both kinds.

**A `Result` never reaches JSON.** `CustomResults.Problem` is the only translation, but nothing enforced
it: an endpoint that returned `Task<Result<T>>` instead of mapping it compiled, passed every gate and
answered 200 with the Result's own members on success and a 500 on failure. Both Result types now carry
`[JsonConverter(typeof(ResultJsonGuard))]`, a converter factory that throws `NotSupportedException`
naming `TypedResults` and `CustomResults.Problem` instead of serialising. Minimal APIs resolve the
response contract when they build the endpoint, so such an endpoint stops the host at boot, and every
in-process test with it; `ProblemDetailsContractTests` proves it with a throwaway endpoint.
