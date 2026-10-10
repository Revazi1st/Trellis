using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Trellis.Api.Common.Http;

/// <summary>
/// A request the framework could not bind — malformed JSON, a missing body, <c>/invoices/not-a-guid</c>,
/// <c>?page=abc</c>, a non-JSON content type, a body over Kestrel's limit — surfaces as a
/// <see cref="BadHttpRequestException"/> because <c>AddApiCommon</c> sets <c>ThrowOnBadRequest</c> in
/// every environment. It is a client error, not an incident: answered with the exception's own status
/// as a problem document (<c>Request.Malformed</c> / <c>Request.TooLarge</c> /
/// <c>Request.UnsupportedMediaType</c>), logged at Information, and kept out of the catch-all 500 handler
/// below it. Registered before <see cref="ProblemDetailsExceptionHandler"/>; order matters.
/// </summary>
internal sealed class BadHttpRequestExceptionHandler(ILogger<BadHttpRequestExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException malformed)
        {
            return false;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Rejected malformed request {Method} {Path} with {StatusCode}: {Reason}",
                httpContext.Request.Method, httpContext.Request.Path, malformed.StatusCode, malformed.Message);
        }

        return await httpContext.WriteProblemAsync(ProblemCatalog.Malformed(malformed), malformed);
    }
}
