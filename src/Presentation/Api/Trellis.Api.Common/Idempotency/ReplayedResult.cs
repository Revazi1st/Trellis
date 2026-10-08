using Microsoft.AspNetCore.Http;
using Trellis.Application.Abstractions;

namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// The stored response of a completed request, sent again: same status, content type, body bytes and
/// <c>Location</c>, plus <c>Idempotency-Replayed: true</c> so a client can tell a replay from a new
/// execution. <c>Location</c> is the only header replayed; everything else (correlation id, security
/// headers) belongs to this response.
/// </summary>
internal sealed class ReplayedResult(StoredResponse stored) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        byte[] body = stored.Body
            ?? throw new InvalidOperationException("Only a stored response with a body can be replayed.");

        HttpResponse response = httpContext.Response;
        response.StatusCode = stored.StatusCode;
        response.Headers[IdempotencyFilter.ReplayedHeaderName] = "true";
        if (stored.Location is not null)
        {
            response.Headers.Location = stored.Location;
        }

        if (body.Length == 0)
        {
            return;
        }

        if (stored.ContentType is not null)
        {
            response.ContentType = stored.ContentType;
        }

        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, httpContext.RequestAborted);
    }
}
