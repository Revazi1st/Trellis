using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trellis.Application.Abstractions;

namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// Executes the endpoint's own result through a <see cref="ResponseCapture"/> and then completes the
/// reservation with what went out: status, content type, body (if within the cap) and <c>Location</c>.
/// A result of 400 or more, or one whose body exceeded the cap, completes the reservation without a
/// body — the fact that the request ended is kept, the response is not replayable. The store ignores a
/// reservation the unit of work never committed, so this is correct for a failed command too.
/// </summary>
internal sealed class CompletingResult(object? inner, string key, IIdempotencyStore store, int maxReplayBodyBytes) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        HttpResponse response = httpContext.Response;
        Stream original = response.Body;
        await using var capture = new ResponseCapture(original, maxReplayBodyBytes);
        response.Body = capture;
        try
        {
            await ToResult(inner).ExecuteAsync(httpContext);
            // A result that wrote through the body writer may have left bytes in its buffer: push them
            // through the capture before it is detached, or the copy (and the client) would miss them.
            await response.BodyWriter.FlushAsync(httpContext.RequestAborted);
        }
        catch (Exception)
        {
            response.Body = original;
            await RecordAsync(httpContext, store, key, StoredResponse.Unavailable(StatusCodes.Status500InternalServerError));
            throw;
        }

        response.Body = original;

        string? location = response.Headers.Location.ToString() is { Length: > 0 } header ? header : null;
        StoredResponse outcome = response.StatusCode < StatusCodes.Status400BadRequest && !capture.Overflowed
            ? new StoredResponse(response.StatusCode, response.ContentType, capture.ToArray(), location)
            : StoredResponse.Unavailable(response.StatusCode, response.ContentType, location);

        await RecordAsync(httpContext, store, key, outcome);
    }

    /// <summary>
    /// Completes the reservation, never with the request's token: the response (or the fault that ended
    /// the request) is what must reach the client. A reservation whose outcome could not be stored reads
    /// as in flight until retention sweeps it — the documented residue, logged so it is not silent.
    /// </summary>
    internal static async Task RecordAsync(HttpContext httpContext, IIdempotencyStore store, string key, StoredResponse outcome)
    {
        try
        {
            await store.CompleteAsync(key, outcome, CancellationToken.None);
        }
        catch (Exception exception)
        {
            httpContext.RequestServices.GetRequiredService<ILogger<IdempotencyFilter>>().LogWarning(
                exception,
                "The outcome of an idempotent {Method} {Path} could not be stored; its key stays in flight until retention sweeps it",
                httpContext.Request.Method,
                httpContext.Request.Path);
        }
    }

    /// <summary>The same conversion the endpoint pipeline applies to a handler's return value.</summary>
    private static IResult ToResult(object? value) => value switch
    {
        null => TypedResults.Empty,
        IResult result => result,
        string text => TypedResults.Text(text),
        _ => TypedResults.Json(value),
    };
}
