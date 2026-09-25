using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Trellis.Api.Common.Http;

/// <summary>
/// Gives every request a correlation id, so one identifier ties together the API's log lines, the
/// trace, and whatever the caller recorded on their side.
/// <para>
/// The id is taken from an inbound <c>X-Correlation-ID</c> header when the client supplies one
/// (that is what makes it correlate across services) and otherwise falls back to the trace
/// identifier. Because an inbound value is attacker-controlled and ends up in log output and in a
/// response header, it is accepted only if it is short and boring: anything else is ignored in
/// favour of a server-generated id. Echoing an arbitrary client string into logs is how log
/// injection and header-splitting bugs start.
/// </para>
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";

    private const int MaxLength = 64;

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId =
            context.Request.Headers.TryGetValue(HeaderName, out Microsoft.Extensions.Primitives.StringValues inbound)
            && IsSafe(inbound.ToString())
                ? inbound.ToString()
                : context.TraceIdentifier;

        context.Response.Headers[HeaderName] = correlationId;

        // Tag the span too: without this the id exists in logs but not in the trace, and the two
        // can only be joined by timestamp.
        Activity.Current?.SetTag("correlation.id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

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
