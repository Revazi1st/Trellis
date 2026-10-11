namespace Trellis.Api.Contracts.Common;

/// <summary>
/// The RFC 9457 problem document the API answers a failed request with: the client-side mirror of the
/// wire shape. Clients branch on <see cref="Code"/> and <see cref="Status"/>, display
/// <see cref="Detail"/> and <see cref="Errors"/>, and never parse <see cref="Title"/> (ADR-0024).
/// <para>
/// A tolerant reader: every member is optional. This API sets <see cref="Type"/>, <see cref="Title"/>,
/// <see cref="Status"/> and <see cref="Code"/> on every problem it writes (ADR-0024; the OpenAPI schema
/// requires exactly those), but a problem can come from an intermediary rather than from this API, and a
/// server older than the client may predate a member. <see cref="Errors"/> is present on a 400 validation
/// problem and on a 400 whose body held a value of the wrong type, and absent otherwise;
/// <see cref="Detail"/> is absent on a bare 404/405.
/// </para>
/// </summary>
/// <param name="Type">URI identifying the problem type.</param>
/// <param name="Title">Short human summary of the problem class. Not for branching.</param>
/// <param name="Status">The HTTP status code the server set.</param>
/// <param name="Detail">Human explanation of this occurrence; safe to show to the user on a 4xx.</param>
/// <param name="Instance">The request path the problem occurred on.</param>
/// <param name="Code">Stable machine discriminator, <c>Area.Problem</c> (e.g. <c>Auth.AccountLockedOut</c>).</param>
/// <param name="CorrelationId">The same value as the <c>X-Correlation-ID</c> response header.</param>
/// <param name="TraceId">W3C trace id of the request.</param>
/// <param name="Errors">The individual failures of a 400: each validation failure, or the body member whose value had the wrong type.</param>
public sealed record ProblemResponse(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Instance,
    string? Code,
    string? CorrelationId,
    string? TraceId,
    IReadOnlyList<FieldError>? Errors);
