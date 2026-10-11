using System.Net.Http.Headers;
using Shouldly;

namespace Trellis.ApiTests;

/// <summary>
/// Assertions on a response's headers that fail when the header is missing. The tempting one-liner,
/// <c>response.Content.Headers.ContentType?.MediaType.ShouldBe(…)</c>, short-circuits the whole chain
/// on a null <c>Content-Type</c>, so the assertion never runs and a bodyless response passes.
/// </summary>
internal static class HttpResponseAssertions
{
    public const string ProblemJson = "application/problem+json";

    /// <summary>The response declares a <c>Content-Type</c> and its media type is <paramref name="mediaType"/>.</summary>
    public static MediaTypeHeaderValue ShouldHaveMediaType(this HttpResponseMessage response, string mediaType)
    {
        MediaTypeHeaderValue contentType = response.Content.Headers.ContentType
            .ShouldNotBeNull($"a {(int)response.StatusCode} response without a Content-Type");
        contentType.MediaType.ShouldBe(mediaType);
        return contentType;
    }

    /// <summary>The response is an RFC 9457 problem document (<c>application/problem+json</c>).</summary>
    public static MediaTypeHeaderValue ShouldBeProblemJson(this HttpResponseMessage response)
        => response.ShouldHaveMediaType(ProblemJson);
}
