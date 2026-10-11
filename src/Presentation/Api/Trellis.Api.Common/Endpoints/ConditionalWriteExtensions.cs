using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Trellis.Api.Common.Endpoints;

/// <summary>
/// Endpoint metadata: this write honours an optional <c>If-Match</c> naming the <c>ETag</c> the caller read,
/// and answers a stale one with a 412. The OpenAPI document reads it to declare the header and the 412
/// (ADR-0025).
/// </summary>
public sealed class ConditionalWriteMetadata
{
    public static readonly ConditionalWriteMetadata Instance = new();

    private ConditionalWriteMetadata()
    {
    }
}

public static class ConditionalWriteExtensions
{
    /// <summary>Declares that the endpoint honours <c>If-Match</c>; pair it with <see cref="TryGetExpectedVersion"/> in the handler.</summary>
    public static RouteHandlerBuilder SupportsConditionalWrite(this RouteHandlerBuilder builder)
        => builder.WithMetadata(ConditionalWriteMetadata.Instance);

    /// <summary>
    /// The row version the request's <c>If-Match</c> expects, for the command's <c>ExpectedVersion</c>
    /// (RFC 9110 §13.1.1). No header, or <c>*</c> (any current representation), is no precondition: true
    /// with null. One strong tag this API issued (<c>"42"</c>, from <see cref="ConditionalGetExtensions.ETag"/>)
    /// is that version. Anything else can never match a current representation, so the answer is false and
    /// the endpoint answers 412: a weak tag (<c>If-Match</c> compares strongly), a list (send the one tag you
    /// read), a tag this API never issued, an unparsable value. The header stays optional: no 428.
    /// </summary>
    public static bool TryGetExpectedVersion(this HttpRequest request, out uint? expectedVersion)
    {
        expectedVersion = null;
        if (!request.Headers.TryGetValue(HeaderNames.IfMatch, out var values))
        {
            return true;
        }

        if (!EntityTagHeaderValue.TryParseList(values, out IList<EntityTagHeaderValue>? tags) || tags.Count != 1)
        {
            return false;
        }

        EntityTagHeaderValue tag = tags[0];
        if (tag.Equals(EntityTagHeaderValue.Any))
        {
            return true;
        }

        // The tag value keeps its quotes: "42".
        if (!tag.IsWeak
            && uint.TryParse(tag.Tag.AsSpan().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out uint version))
        {
            expectedVersion = version;
            return true;
        }

        return false;
    }
}
