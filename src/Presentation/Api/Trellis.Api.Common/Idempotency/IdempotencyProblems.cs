using Microsoft.AspNetCore.Http;
using Trellis.Api.Common.Http;

namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// The one idempotency outcome that is not an <c>Error</c>: reusing a key for a different request is a
/// 422, and a 422 has no <c>ErrorType</c> to travel through <c>CustomResults</c>. It is written like every
/// other non-Result situation, with <c>HttpContext.WriteProblemAsync</c>, and sits with the filter rather
/// than in <c>ProblemCatalog</c> because its siblings are the <see cref="Application.Abstractions.IdempotencyErrors"/>
/// catalogue (ADR-0018, ADR-0024).
/// </summary>
public static class IdempotencyProblems
{
    public static readonly ProblemDescriptor FingerprintMismatch = new(
        StatusCodes.Status422UnprocessableEntity,
        "Idempotency.FingerprintMismatch",
        "Unprocessable request",
        "This idempotency key was already used for a different request (another method, path or body). A retry must repeat the original request; a new request needs a new key.");
}
