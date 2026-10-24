using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Trellis.Application.Abstractions;

namespace Trellis.Api.Common.Http;

/// <summary>
/// Names the authenticated caller on the signals the rest of the request produces, so a rejected command,
/// a failed write or a 403 says which account sent it. Two places, one value, the caller's user id:
/// <list type="bullet">
/// <item>a <see cref="ScopeProperty"/> log scope, which every line written further in inherits: the
/// use-case outcome line and whatever a handler logs. ServiceDefaults exports scopes as log attributes
/// (<c>IncludeScopes</c>). The exception handler runs further out, so its line carries the correlation id
/// and the trace, not this;</item>
/// <item>the <see cref="SpanAttribute"/> attribute on the request span, OpenTelemetry's name for the end user
/// an operation runs for, so a trace search by user finds the request around its use-case span. A 403 from
/// authorization has no use case and, at the shipped levels, no log line (ASP.NET Core writes the failure
/// at Information under <c>Microsoft.AspNetCore</c>, which <c>appsettings.json</c> holds at Warning), so
/// its span is what names the caller.</item>
/// </list>
/// The id only, never the name or the e-mail: the id is a pseudonymous key the database already uses, the
/// e-mail is personal data that the log backend would keep for as long as it keeps logs (ADR-0028). An
/// anonymous request, or a principal without a user id, adds nothing.
/// </summary>
public sealed class UserTelemetryMiddleware(RequestDelegate next, ILogger<UserTelemetryMiddleware> logger)
{
    /// <summary>
    /// The log scope property that carries the caller's user id. Not <c>UserId</c>: a message's <c>{UserId}</c>
    /// is the account the line is about (the security events), which need not be the caller, and one record
    /// cannot hold both under one key. <c>TelemetryNamingTests</c> keeps every message template off this name.
    /// </summary>
    public const string ScopeProperty = "CallerUserId";

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
