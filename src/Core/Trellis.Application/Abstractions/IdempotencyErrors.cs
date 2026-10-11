using Trellis.SharedKernel.Results;

namespace Trellis.Application.Abstractions;

/// <summary>
/// The whole catalogue of <c>Idempotency.*</c> outcomes that are <see cref="Error"/>s (ADR-0018, ADR-0024),
/// one owner per code. It lives in Application rather than beside the API filter because the unit of work
/// produces one of them: a second first request losing the race at the <c>IdempotencyKeys</c> primary key
/// comes back from the commit as <see cref="Duplicate"/>. The outcomes about the header itself are produced
/// only by the API's filter, but they sit here too, so that one code is never declared in two places. Every
/// member reaches the client through <c>CustomResults.Problem</c>, carrying its code as the problem's
/// <c>code</c> and <c>type</c>. The one idempotency outcome that is not an <see cref="Error"/> is the
/// fingerprint mismatch: a 422 has no <see cref="ErrorType"/>, so the API filter writes it from its own
/// <c>ProblemDescriptor</c> (<c>Idempotency.FingerprintMismatch</c>).
/// </summary>
public static class IdempotencyErrors
{
    /// <summary>
    /// Every unusable key shares this code: blank, too long, a repeated header or a comma-separated list. The
    /// client's remedy is the same for each (send one usable key, or omit the header), so it branches on one
    /// code and reads the description for which rule it broke.
    /// </summary>
    public const string InvalidKeyCode = "Idempotency.InvalidKey";

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

    /// <summary>The header was sent with nothing usable in it; running without the guarantee would turn the caller's retry into a second apply.</summary>
    public static readonly Error BlankKey = Error.Validation(
        InvalidKeyCode,
        "The idempotency key must not be blank. Omit the Idempotency-Key header to send the request without idempotency.");

    /// <summary>
    /// The header was sent twice, or as a comma-separated list. Joining the values would make a key the caller
    /// never sent, and a retry that sends them in another order would be a different request.
    /// </summary>
    public static readonly Error MultipleKeys = Error.Validation(
        InvalidKeyCode,
        "Send exactly one Idempotency-Key value: a repeated header or a comma-separated list is not a key.");

    /// <summary>The endpoint declared <c>RequireIdempotency(keyRequired: true)</c> and the request carried no key.</summary>
    public static readonly Error KeyRequired = Error.Validation(
        "Idempotency.KeyRequired",
        "This endpoint requires an Idempotency-Key header: send a unique value per logical request and repeat it on a retry.");

    public static Error KeyTooLong(int maxLength) => Error.Validation(
        InvalidKeyCode,
        $"The idempotency key must be at most {maxLength} characters long.");
}
