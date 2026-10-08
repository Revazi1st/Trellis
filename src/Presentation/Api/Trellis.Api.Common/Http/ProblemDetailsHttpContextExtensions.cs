using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Trellis.Api.Common.Http;

/// <summary>
/// The one way a non-Result path (an authentication callback, the rate limiter, an exception handler)
/// writes a problem document: through <see cref="IProblemDetailsService"/>, so the enrichment registered
/// by <c>AddApiCommon</c> (<c>type</c>, <c>instance</c>, <c>correlationId</c>, <c>traceId</c>) applies to it
/// exactly as it does to every <c>CustomResults.Problem</c> response (ADR-0024).
/// </summary>
public static class ProblemDetailsHttpContextExtensions
{
    /// <summary>
    /// Sets the status and writes <paramref name="problem"/>. False only when the caller's <c>Accept</c>
    /// header admits neither JSON nor problem+json; the response is then a bodyless status, which is the
    /// correct answer for a client that refused the only representation this API has.
    /// </summary>
    public static async ValueTask<bool> WriteProblemAsync(
        this HttpContext httpContext,
        ProblemDescriptor problem,
        Exception? exception = null)
    {
        httpContext.Response.StatusCode = problem.Status;

        return await httpContext.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails =
                {
                    Status = problem.Status,
                    Title = problem.Title,
                    Detail = problem.Detail,
                    Extensions = { [ProblemExtensions.Code] = problem.Code },
                },
            });
    }
}
