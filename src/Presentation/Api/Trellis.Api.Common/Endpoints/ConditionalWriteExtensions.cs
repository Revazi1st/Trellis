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
    /// the endpoint answers 412 without looking the resource up: a weak tag (<c>If-Match</c> compares
    /// strongly), a list (send the one tag you read), a tag this API never issued, an unparsable value. A tag
    /// is compared as the opaque string it is, so only the exact text the API emits names a version:
    /// <c>"042"</c> is not <c>"42"</c>. The header stays optional: no 428.
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

        if (!tag.IsWeak && TryParseVersion(tag, out uint version))
        {
            expectedVersion = version;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The version a strong tag names, only when the tag round-trips: digits alone (no sign, no spaces)
    /// whose value prints back as the same text, so a leading zero is a tag this API never issued.
    /// </summary>
    private static bool TryParseVersion(EntityTagHeaderValue tag, out uint version)
    {
        version = 0;
        if (tag.Tag.Length < 2)
        {
            return false;
        }

        // The tag keeps its quotes: "42".
        ReadOnlySpan<char> opaque = tag.Tag.AsSpan()[1..^1];
        return uint.TryParse(opaque, NumberStyles.None, CultureInfo.InvariantCulture, out version)
               && opaque.SequenceEqual(version.ToString(CultureInfo.InvariantCulture));
    }
}
