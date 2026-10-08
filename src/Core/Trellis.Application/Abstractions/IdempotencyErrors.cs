using Trellis.SharedKernel.Results;

namespace Trellis.Application.Abstractions;

/// <summary>
/// The catalogue of <c>Idempotency.*</c> outcomes (ADR-0018, ADR-0024). Here rather than in the API
/// because the unit of work produces one of them: a second first request losing the race at the
/// <c>IdempotencyKeys</c> primary key comes back from the commit as <see cref="Duplicate"/>. Every
/// member reaches the client through <c>CustomResults.Problem</c>, carrying its code as the problem's
/// <c>code</c> and <c>type</c>. The one idempotency outcome that is not an <see cref="Error"/> is the
/// fingerprint mismatch: a 422 has no <see cref="ErrorType"/>, so the API filter writes it from its own
/// <c>ProblemDescriptor</c> (<c>Idempotency.FingerprintMismatch</c>).
/// </summary>
public static class IdempotencyErrors
{
    /// <summary>
    /// The key's request has already been processed but its response is not available for replay: it
    /// exceeded the replay cap, it ended in a failure after its command had committed, or it lost the
    /// commit race to a concurrent request with the same key. The command applied exactly once either
    /// way; the client reads the resource to learn the outcome.
    /// </summary>
    public static readonly Error Duplicate = Error.Conflict(
        "Idempotency.Duplicate",
        "A request with this idempotency key has already been processed, and its response is not available for replay. Read the resource to learn the outcome.");

    /// <summary>The first request with this key is still running (or died after its commit, until retention sweeps the row). Carries <c>Retry-After</c>.</summary>
    public static readonly Error InFlight = Error.Conflict(
        "Idempotency.InFlight",
        "A request with this idempotency key is still being processed. Retry after the interval in the Retry-After header.");

    /// <summary>Both key errors share one code: the client's remedy is the same (send a usable key, or omit the header).</summary>
    public static readonly Error BlankKey = Error.Validation(
        "Idempotency.InvalidKey",
        "The idempotency key must not be blank. Omit the Idempotency-Key header to send the request without idempotency.");

    public static Error KeyTooLong(int maxLength) => Error.Validation(
        "Idempotency.InvalidKey",
        $"The idempotency key must be at most {maxLength} characters long.");
}
