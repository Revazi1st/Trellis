using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Trellis.Api.Common.Http;
using Trellis.SharedKernel.Results;
using WireFieldError = Trellis.Api.Contracts.Common.FieldError;

namespace Trellis.Api.Common.Results;

/// <summary>
/// Maps SharedKernel errors onto RFC 9457 problem details — the single translation point from Result to
/// HTTP (ADR-0004, shape in ADR-0024). The document carries a human <c>title</c> per error class, the
/// error's description as <c>detail</c>, its code as the <c>code</c> extension and, for a 400, an
/// <c>errors[]</c> of <c>{ field, code, description }</c>: one entry per <see cref="FieldError"/> (field =
/// the request's JSON path) or per whole-request error (field = null) — a single plain validation error
/// included, so clients can always render <c>errors[]</c> on a 400. <c>type</c>, <c>instance</c>,
/// <c>correlationId</c> and <c>traceId</c> are added once, for every problem, by the enrichment in
/// <c>AddApiCommon</c>: the result writes through <see cref="IProblemDetailsService"/>.
/// </summary>
public static class CustomResults
{
    public static ProblemHttpResult Problem(Error error)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ProblemExtensions.Code] = error.Code,
        };

        if (error.Type == ErrorType.Validation)
        {
            IEnumerable<Error> failures = error is ValidationError validation ? validation.Errors : [error];
            extensions[ProblemExtensions.Errors] = failures
                .Select(static e => new WireFieldError(
                    e is FieldError field ? JsonFieldPath.From(field.Field) : null,
                    e.Code,
                    e.Description))
                .ToArray();
        }

        return TypedResults.Problem(
            title: ProblemTitles.For(error.Type),
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
