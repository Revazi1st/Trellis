using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Trellis.Api.Common.Http;

/// <summary>
/// Last-resort handler: an unhandled exception becomes the opaque <c>Server.Failure</c> 500 — the
/// message, type and stack stay in the Error log line and never reach the client. Expected failures never
/// get here (they flow as Results), and a request the framework could not bind is answered by
/// <see cref="BadHttpRequestExceptionHandler"/> first.
/// </summary>
internal sealed class ProblemDetailsExceptionHandler(ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);

        return await httpContext.WriteProblemAsync(ProblemCatalog.ServerFailure, exception);
    }
}
