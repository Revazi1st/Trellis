using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Trellis.Api.Common.Http;

/// <summary>
/// Gives every request a correlation id, so one identifier ties together the API's log lines, the
/// trace, and whatever the caller recorded on their side.
/// <para>
/// The id is taken from an inbound <c>X-Correlation-ID</c> header when the client supplies one
/// (that is what makes it correlate across services) and otherwise falls back to the W3C trace id of
/// the request: the id every hop of the distributed trace shares and the one a tracing backend
/// searches by. A problem response's <c>traceId</c> (<c>00-{trace id}-{span id}-{flags}</c>) carries the
/// same trace id, so a caller holding either can find the other. Kestrel's per-request
/// <c>TraceIdentifier</c> is only the last resort, for a request without a W3C activity. Because an
/// inbound value is attacker-controlled and ends up in log output and in a response header, it is
/// accepted only if it is short and boring: anything else is ignored in favour of the server-side id.
/// Echoing an arbitrary client string into logs is how log injection and header-splitting bugs start.
/// </para>
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>The <see cref="HttpContext.Items"/> key under which the id is kept for the rest of the request (problem documents read it back).</summary>
    public const string ItemKey = "CorrelationId";

    /// <summary>The span attribute carrying the id, so logs and the trace join without a timestamp search.</summary>
    public const string SpanAttribute = "trellis.correlation_id";

    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId =
            context.Request.Headers.TryGetValue(HeaderName, out Microsoft.Extensions.Primitives.StringValues inbound)
            && IsSafe(inbound.ToString())
                ? inbound.ToString()
                : ServerSideId(context);

        context.Items[ItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        // Re-assert the header when the response starts: the exception-handler middleware clears the
        // response (headers included) before it writes its problem document, and that document carries
        // the correlationId — the header must still say the same thing.
        context.Response.OnStarting(static state =>
        {
            (HttpResponse response, string id) = ((HttpResponse, string))state;
            response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        }, (context.Response, correlationId));

        // Tag the span too: without this the id exists in logs but not in the trace, and the two
        // can only be joined by timestamp.
        Activity.Current?.SetTag(SpanAttribute, correlationId);

        using (logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { [ItemKey] = correlationId }))
        {
            await next(context);
        }
    }

    /// <summary>The request's correlation id, once the middleware has run.</summary>
    public static bool TryGet(HttpContext context, [NotNullWhen(true)] out string? correlationId)
    {
        if (context.Items.TryGetValue(ItemKey, out object? value) && value is string id)
        {
            correlationId = id;
            return true;
        }

        correlationId = null;
        return false;
    }

    /// <summary>The request's W3C trace id; Kestrel's request identifier only when there is no W3C activity.</summary>
    private static string ServerSideId(HttpContext context)
        => Activity.Current is { IdFormat: ActivityIdFormat.W3C } request
            ? request.TraceId.ToHexString()
            : context.TraceIdentifier;

    /// <summary>ASCII letters/digits plus <c>- _ . :</c> — covers GUIDs, ULIDs and W3C trace ids — capped at 64 chars.</summary>
    private static bool IsSafe(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaxLength
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}

/// <summary>Registration helper so hosts don't have to name the middleware type.</summary>
public static class CorrelationIdExtensions
{
    /// <summary>
    /// Adds correlation-id handling. Place it FIRST, before exception handling and request logging:
    /// anything that runs earlier produces log lines without the id, which are exactly the lines you
    /// want when something fails.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
        => app.UseMiddleware<CorrelationIdMiddleware>();
}
