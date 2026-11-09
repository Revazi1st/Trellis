using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;
using Trellis.Api.Contracts.Common;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// Assertions on a response that say what went wrong. A status checked by an arrange step fails naming the
/// request, the status and the problem's <c>code</c> (<see cref="ShouldAnswerAsync"/>, which every
/// <c>{Feature}HttpDriver</c> uses). A header check fails when the header is missing: the tempting one-liner,
/// <c>response.Content.Headers.ContentType?.MediaType.ShouldBe(…)</c>, short-circuits the whole chain on a
/// null <c>Content-Type</c>, so the assertion never runs and a bodyless response passes.
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

    /// <summary>
    /// Fails unless the API answered <paramref name="expected"/>, naming the request, what it answered instead,
    /// the problem's <c>code</c> and the body: an arrange step that a refusal stopped says why.
    /// </summary>
    public static async Task ShouldAnswerAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string? code = null;
        try
        {
            code = JsonSerializer.Deserialize<ProblemResponse>(body, JsonSerializerOptions.Web)?.Code;
        }
        catch (JsonException)
        {
            // Not a problem document: the body below says what it is.
        }

        throw new ShouldAssertException(
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}{Environment.NewLine}" +
            $"    should answer {(int)expected} but answered {(int)response.StatusCode} {code ?? "(no problem code)"}{Environment.NewLine}" +
            $"    {body}");
    }
}
