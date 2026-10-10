using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Trellis.Api.Common.Endpoints;

/// <summary>
/// Endpoint metadata: this GET emits an <c>ETag</c> and answers <c>If-None-Match</c> with a 304. The
/// OpenAPI document reads it to declare the header, the response header and the 304 (ADR-0025).
/// </summary>
public sealed class ConditionalGetMetadata
{
    public static readonly ConditionalGetMetadata Instance = new();

    private ConditionalGetMetadata()
    {
    }
}

public static class ConditionalGetExtensions
{
    /// <summary>Declares that the endpoint implements conditional GET; pair it with <see cref="IsNotModified"/> in the handler.</summary>
    public static RouteHandlerBuilder SupportsConditionalGet(this RouteHandlerBuilder builder)
        => builder.WithMetadata(ConditionalGetMetadata.Instance);

    /// <summary>A weak validator over a row version: <c>W/"42"</c>.</summary>
    public static EntityTagHeaderValue WeakETag(long version)
        => new(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\"{version}\""), isWeak: true);

    /// <summary>
    /// True when the request's <c>If-None-Match</c> names <paramref name="current"/> (RFC 9110 §13.1.2):
    /// the comparison is weak, so <c>W/"42"</c> and <c>"42"</c> both match a weak <c>W/"42"</c>; a list
    /// matches when any member does; <c>*</c> matches any existing representation. An absent or
    /// unparsable header never matches, so the caller serves the full response.
    /// </summary>
    public static bool IsNotModified(this HttpRequest request, EntityTagHeaderValue current)
        => EntityTagHeaderValue.TryParseList(request.Headers.IfNoneMatch, out IList<EntityTagHeaderValue>? tags)
           && tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: false));
}
