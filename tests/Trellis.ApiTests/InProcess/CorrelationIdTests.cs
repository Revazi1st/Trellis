using Shouldly;
using Trellis.Api.Common.Http;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The correlation id is the one piece of request state a caller can influence, so the tests that
/// matter are the ones about NOT trusting it: a sane inbound id is honoured, anything else is
/// replaced by a server-generated one rather than echoed back into logs and headers.
/// </summary>
public sealed class CorrelationIdTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_well_formed_inbound_correlation_id_is_echoed_back()
    {
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "01JB8Z5M9K-trace.id:42");

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)
            .ShouldHaveSingleItem()
            .ShouldBe("01JB8Z5M9K-trace.id:42");
    }

    [Theory]
    // Newlines are the header-splitting / log-injection payload; the rest are simply not id-shaped.
    [InlineData("has spaces")]
    [InlineData("injected\r\nX-Evil: 1")]
    [InlineData("<script>alert(1)</script>")]
    public async Task An_unsafe_inbound_correlation_id_is_replaced_not_reflected(string unsafeValue)
    {
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        // TryAddWithoutValidation: HttpClient itself rejects some of these, and the point is to prove
        // the SERVER does not reflect them even when they arrive on the wire.
        request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, unsafeValue);

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        string echoed = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single();
        string.Equals(echoed, unsafeValue, StringComparison.Ordinal).ShouldBeFalse();
        echoed.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_request_without_a_correlation_id_still_gets_one()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)
            .ShouldHaveSingleItem()
            .ShouldNotBeNullOrWhiteSpace();
    }
}
