using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Shouldly;
using Trellis.Api.Common.Http;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The correlation id is the one piece of request state a caller can influence, so the tests that
/// matter are the ones about NOT trusting it: a sane inbound id is honoured, anything else is
/// replaced by the server-side id rather than echoed back into logs and headers. The server-side id is
/// the request's W3C trace id, the same trace a problem body's <c>traceId</c> names.
/// </summary>
public sealed class CorrelationIdTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string W3CTraceId = "^[0-9a-f]{32}$";

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
        echoed.ShouldMatch(W3CTraceId);
    }

    [Fact]
    public async Task A_request_without_a_correlation_id_still_gets_one()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/alive", TestContext.Current.CancellationToken);

        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)
            .ShouldHaveSingleItem()
            .ShouldMatch(W3CTraceId);
    }

    [Fact]
    public async Task Without_an_inbound_id_the_header_names_the_trace_of_the_problem_body()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);

        using HttpResponseMessage response = await admin.GetAsync($"/api/v1/invoices/{Guid.NewGuid()}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        string header = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).ShouldHaveSingleItem();
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        string traceId = problem.RootElement.GetProperty("traceId").GetString().ShouldNotBeNull();

        // The framework writes the request activity's id in traceparent form (version, trace id, span id and
        // flags, dash-separated). The correlation id is its trace-id field, the part every hop shares.
        ActivityContext.TryParse(traceId, traceState: null, out ActivityContext trace).ShouldBeTrue(traceId);
        header.ShouldBe(trace.TraceId.ToHexString());

        // And the body says the same thing as the header, so a caller holding either can find the other (ADR-0024).
        problem.RootElement.GetProperty(ProblemExtensions.CorrelationId).GetString().ShouldBe(header);
    }

    [Fact]
    public async Task An_inbound_correlation_id_reaches_the_problem_body_as_well_as_the_header()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/invoices/{Guid.NewGuid()}");
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "01JB8Z5M9K-trace.id:42");

        using HttpResponseMessage response = await admin.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Headers.GetValues(CorrelationIdMiddleware.HeaderName).ShouldHaveSingleItem().ShouldBe("01JB8Z5M9K-trace.id:42");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        problem.RootElement.GetProperty(ProblemExtensions.CorrelationId).GetString().ShouldBe("01JB8Z5M9K-trace.id:42");
    }
}
