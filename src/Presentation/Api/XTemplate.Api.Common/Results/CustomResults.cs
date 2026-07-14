using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Api.Common.Results;

/// <summary>Maps SharedKernel errors onto RFC 9457 problem details — the single translation point from Result to HTTP (ADR-0004).</summary>
public static class CustomResults
{
    public static ProblemHttpResult Problem(Error error)
    {
        Dictionary<string, object?>? extensions = error is ValidationError validationError
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["errors"] = validationError.Errors
                    .Select(static e => new { e.Code, e.Description })
                    .ToArray(),
            }
            : null;

        return TypedResults.Problem(
            title: error.Code,
            detail: error.Description,
            statusCode: StatusCodeFor(error.Type),
            extensions: extensions);
    }

    private static int StatusCodeFor(ErrorType errorType) => errorType switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status500InternalServerError,
    };
}
