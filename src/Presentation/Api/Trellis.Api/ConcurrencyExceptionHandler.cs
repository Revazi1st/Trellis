using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Trellis.Api;

/// <summary>
/// Turns EF Core optimistic-concurrency conflicts (the Invoice xmin token) into a 409 ProblemDetails.
/// Lives in the host, not Api.Common, so Api.Common stays free of EF Core (ADR-0002).
/// Registered before the catch-all ProblemDetailsExceptionHandler so it runs first.
/// </summary>
internal sealed class ConcurrencyExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Title = "Concurrency conflict",
                Detail = "The resource was modified by someone else. Reload and try again.",
                Status = StatusCodes.Status409Conflict,
            },
        });
    }
}
