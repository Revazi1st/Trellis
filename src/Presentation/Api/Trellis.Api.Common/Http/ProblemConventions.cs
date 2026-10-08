using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Trellis.SharedKernel.Results;

namespace Trellis.Api.Common.Http;

/// <summary>
/// The <c>type</c> member of every problem document: a URN built from the problem's <c>code</c>, so one
/// identifier is spelled the same way in <c>code</c>, <c>type</c>, logs and docs. A URN because nothing is
/// served at it (RFC 9457 §4 allows exactly that); a product that later publishes problem documentation
/// changes <see cref="Prefix"/> to its URL and nothing else (ADR-0024).
/// </summary>
public static class ProblemTypes
{
    public const string Prefix = "urn:problem-type:";

    public static string For(string code) => Prefix + code;
}

/// <summary>Names of the extension members this API adds to the RFC 9457 document (the standard members keep their RFC names).</summary>
public static class ProblemExtensions
{
    /// <summary>The stable machine discriminator, <c>Area.Problem</c>: what clients branch on.</summary>
    public const string Code = "code";

    /// <summary>The per-field failures of a 400 produced from a <see cref="Result"/>: <c>[{ field, code, description }]</c>.</summary>
    public const string Errors = "errors";

    /// <summary>The same value as the <c>X-Correlation-ID</c> response header.</summary>
    public const string CorrelationId = "correlationId";

    /// <summary>Written by ASP.NET's problem-details defaults: the request activity's id in traceparent form.</summary>
    public const string TraceId = "traceId";
}

/// <summary>The human <c>title</c> per error class: a short phrase, no trailing period, never something to branch on.</summary>
public static class ProblemTitles
{
    public static string For(ErrorType type) => type switch
    {
        ErrorType.Validation => "Validation failed",
        ErrorType.NotFound => "Not found",
        ErrorType.Conflict => "Conflict",
        ErrorType.Unauthorized => "Unauthorized",
        ErrorType.Forbidden => "Forbidden",
        _ => "Server failure",
    };

    public static string ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status405MethodNotAllowed => "Method not allowed",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status413PayloadTooLarge => "Request too large",
        StatusCodes.Status415UnsupportedMediaType => "Unsupported media type",
        StatusCodes.Status429TooManyRequests => "Too many requests",
        StatusCodes.Status500InternalServerError => "Server failure",
        StatusCodes.Status504GatewayTimeout => "Request timed out",
        _ => ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } phrase ? phrase : "Request failed",
    };
}

/// <summary>One situation the API reports that does not originate from a <see cref="Result"/>: its status, code, title and detail.</summary>
public sealed record ProblemDescriptor(int Status, string Code, string Title, string? Detail);

/// <summary>
/// Everything the API says that does not come through <c>CustomResults.Problem(Error)</c>: one row per
/// situation, so there is one place to read the vocabulary. A new non-Result situation gets a row here
/// and is written with <c>HttpContext.WriteProblemAsync(ProblemDescriptor)</c>, never with
/// <c>TypedResults.Problem</c> or <c>WriteAsJsonAsync</c>. Outcomes that are Results (including the unit
/// of work's <c>Concurrency.Conflict</c>) are not catalogued: their <c>*Errors</c> class is the catalogue.
/// </summary>
public static class ProblemCatalog
{
    public static readonly ProblemDescriptor Unauthenticated = new(
        StatusCodes.Status401Unauthorized,
        "Auth.Unauthenticated",
        "Unauthorized",
        "A valid access token is required. Obtain one from POST /auth/login.");

    public static readonly ProblemDescriptor Forbidden = new(
        StatusCodes.Status403Forbidden,
        "Auth.Forbidden",
        "Forbidden",
        "The authenticated user lacks the permission this endpoint requires.");

    /// <summary>The opaque 500: the exception's message never reaches the client.</summary>
    public static readonly ProblemDescriptor ServerFailure = new(
        StatusCodes.Status500InternalServerError,
        "Server.Failure",
        "Server failure",
        "An unexpected error occurred.");

    public static readonly ProblemDescriptor RequestTooLarge = new(
        StatusCodes.Status413PayloadTooLarge,
        "Request.TooLarge",
        "Request too large",
        "The request body exceeds the size this API accepts.");

    public static readonly ProblemDescriptor UnsupportedMediaType = new(
        StatusCodes.Status415UnsupportedMediaType,
        "Request.UnsupportedMediaType",
        "Unsupported media type",
        "The request body must be JSON (application/json).");

    public static ProblemDescriptor RateLimited(TimeSpan? retryAfter) => new(
        StatusCodes.Status429TooManyRequests,
        "RateLimit.Exceeded",
        "Too many requests",
        retryAfter is { } wait
            ? $"Rate limit exceeded. Retry after {Math.Ceiling(wait.TotalSeconds)} seconds."
            : "Rate limit exceeded. Retry after the interval in the Retry-After header.");

    /// <summary>
    /// A request the framework could not bind: malformed or missing JSON, an unparsable route or query value,
    /// an oversized body, a non-JSON content type. The exception's message names a parameter or a limit,
    /// never a stack, so it is the <c>detail</c>.
    /// </summary>
    public static ProblemDescriptor Malformed(BadHttpRequestException exception) => exception.StatusCode switch
    {
        StatusCodes.Status413PayloadTooLarge => RequestTooLarge with { Detail = exception.Message },
        StatusCodes.Status415UnsupportedMediaType => UnsupportedMediaType with { Detail = exception.Message },
        var status => new(status, "Request.Malformed", "Malformed request", exception.Message),
    };

    /// <summary>
    /// The code and title for a response that reached the client with a status but no body (an unknown
    /// route's 404, a 405, the request-timeout 504, Kestrel's 413): <c>Http.{ReasonPhrase}</c> unless the
    /// status has a row of its own, so a problem never leaves without a <c>code</c>.
    /// </summary>
    public static ProblemDescriptor ForStatus(int status) => status switch
    {
        StatusCodes.Status413PayloadTooLarge => RequestTooLarge,
        StatusCodes.Status415UnsupportedMediaType => UnsupportedMediaType,
        _ => new(status, "Http." + HttpCodeSuffix(status), ProblemTitles.ForStatus(status), Detail: null),
    };

    private static string HttpCodeSuffix(int status)
    {
        string phrase = ReasonPhrases.GetReasonPhrase(status).Replace(" ", string.Empty, StringComparison.Ordinal);
        return phrase.Length > 0 ? phrase : "Status" + status;
    }
}
