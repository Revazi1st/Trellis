using Trellis.Api.Contracts.Common;
using Trellis.SharedKernel.Results;

namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// The idempotency filter's outcomes as <see cref="Error"/>s, so the filter answers through the same
/// <c>CustomResults.Problem</c> seam as every endpoint and its problems carry the same <c>code</c>,
/// <c>type</c>, <c>errors[]</c> and <c>correlationId</c> (ADR-0024). Both key errors share one code: the
/// client's remedy is the same (send a usable key, or omit the header).
/// </summary>
public static class IdempotencyErrors
{
    public static readonly Error Duplicate = Error.Conflict(
        "Idempotency.Duplicate",
        "Duplicate request: a request with this Idempotency-Key has already been processed.");

    public static readonly Error BlankKey = Error.Validation(
        "Idempotency.InvalidKey",
        $"The {ApiHeaders.IdempotencyKey} header must not be blank. Omit it to send the request without idempotency.");

    public static Error KeyTooLong(int maxLength) => Error.Validation(
        "Idempotency.InvalidKey",
        $"The {ApiHeaders.IdempotencyKey} header must be at most {maxLength} characters long.");
}
