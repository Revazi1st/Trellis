using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Trellis.Application.Abstractions;

namespace Trellis.Api.Common.Http;

/// <summary>
/// Names the authenticated caller on every signal the rest of the request produces, so a 403, a rejected
/// command or a failed write says which account sent it. Two places, one value, the caller's user id:
/// <list type="bullet">
/// <item>a <see cref="ScopeProperty"/> log scope, which every line written further in inherits: the
/// use-case outcome line, the authorization failure, whatever a handler logs. ServiceDefaults exports
/// scopes as log attributes (<c>IncludeScopes</c>), and Serilog reads them from the log context;</item>
/// <item>the <see cref="SpanAttribute"/> attribute on the request span, OpenTelemetry's name for the end user
/// an operation runs for, so a trace search by user finds the request around its use-case span.</item>
/// </list>
/// The id only, never the name or the e-mail: the id is a pseudonymous key the database already uses, the
/// e-mail is personal data that the log backend would keep for as long as it keeps logs (ADR-0028). An
/// anonymous request, or a principal without a user id, adds nothing.
/// </summary>
public sealed class UserTelemetryMiddleware(RequestDelegate next, ILogger<UserTelemetryMiddleware> logger)
{
    /// <summary>The log scope property that carries the caller's user id.</summary>
    public const string ScopeProperty = "UserId";

    /// <summary>The span attribute that carries it: the OpenTelemetry semantic convention for the end user's id.</summary>
    public const string SpanAttribute = "enduser.id";

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser)
    {
        if (currentUser.UserId is not { } userId)
        {
            await next(context);
            return;
        }

        string id = userId.ToString();
        Activity.Current?.SetTag(SpanAttribute, id);

        using (logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal) { [ScopeProperty] = id }))
        {
            await next(context);
        }
    }
}

/// <summary>Registration helper so hosts don't have to name the middleware type.</summary>
public static class UserTelemetryExtensions
{
    /// <summary>
    /// Adds the caller's user id to the log scope and the request span. Place it right after
    /// <c>UseAuthentication</c>: before it nobody is signed in yet, and every stage after it (rate
    /// limiting, authorization, the endpoint) should log with the id.
    /// </summary>
    public static IApplicationBuilder UseUserTelemetry(this IApplicationBuilder app)
        => app.UseMiddleware<UserTelemetryMiddleware>();
}
