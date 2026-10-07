namespace Trellis.Api.Contracts.Common;

/// <summary>
/// The RFC 9457 problem document the API answers a failed request with: the client-side mirror of the
/// wire shape. Clients branch on <see cref="Code"/> and <see cref="Status"/>, display
/// <see cref="Detail"/> and <see cref="Errors"/>, and never parse <see cref="Title"/> (ADR-0004).
/// <para>
/// A tolerant reader: every member is optional, because not every writer emits every member yet
/// (<c>code</c>, <c>type</c> and <c>correlationId</c> are still missing from some or all of them) and a
/// problem can come from an intermediary rather than from this API. <see cref="Errors"/> is present on
/// 400 validation problems and absent otherwise.
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
/// <param name="Errors">The individual validation failures of a 400.</param>
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
