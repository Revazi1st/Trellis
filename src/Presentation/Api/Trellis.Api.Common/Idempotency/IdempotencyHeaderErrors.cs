using Trellis.SharedKernel.Results;

namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// The idempotency outcomes only <see cref="IdempotencyFilter"/> can produce, about the header itself.
/// The rest of the <c>Idempotency.*</c> catalogue lives in Application's <c>IdempotencyErrors</c>, where
/// the unit of work can reach it (ADR-0018); these two never leave the API, so they sit beside the filter.
/// Both reach the client through <c>CustomResults.Problem</c> like every other Result (ADR-0024).
/// </summary>
public static class IdempotencyHeaderErrors
{
    /// <summary>
    /// The header was sent twice, or as a list. Its own code stays <c>Idempotency.InvalidKey</c>: the remedy
    /// is the one the blank and too-long keys share, send one usable key or omit the header.
    /// </summary>
    public static readonly Error MultipleKeys = Error.Validation(
        "Idempotency.InvalidKey",
        "Send exactly one Idempotency-Key value: a repeated header or a comma-separated list is not a key.");

    /// <summary>The endpoint declared <c>RequireIdempotency(keyRequired: true)</c> and the request carried no key.</summary>
    public static readonly Error KeyRequired = Error.Validation(
        "Idempotency.KeyRequired",
        "This endpoint requires an Idempotency-Key header: send a unique value per logical request and repeat it on a retry.");
}
