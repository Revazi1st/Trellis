using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Infrastructure;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// The reference outbound integration (ADR-0020), exercised through the real registration in
/// <c>AddInfrastructure</c> — options binding, the typed client and the resilience pipeline all
/// included — with only the socket replaced. The contract under test is that nothing a third party
/// does reaches Application code as an exception: every outcome is a <see cref="Result{T}"/>.
/// </summary>
public sealed class ExchangeRateProviderTests
{
    [Fact]
    public async Task Provider_returns_the_quoted_rate()
    {
        IExchangeRateProvider provider = BuildProvider(
            new StubHandler(_ => Json("""{"base":"EUR","rates":{"USD":1.0825}}""")));

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(1.0825m);
    }

    [Fact]
    public async Task Provider_maps_an_error_status_to_Unavailable()
    {
        // 404 rather than 500 on purpose: the standard resilience handler retries 5xx, and this test
        // is about the mapping, not the retry budget.
        IExchangeRateProvider provider = BuildProvider(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.Unavailable);
    }

    [Fact]
    public async Task Provider_maps_unreadable_json_to_MalformedResponse()
    {
        IExchangeRateProvider provider = BuildProvider(new StubHandler(_ => Json("not json at all")));

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.MalformedResponse);
    }

    [Fact]
    public async Task Provider_reports_a_currency_the_feed_does_not_publish()
    {
        IExchangeRateProvider provider = BuildProvider(
            new StubHandler(_ => Json("""{"base":"EUR","rates":{"USD":1.08}}""")));

        Result<decimal> result = await provider.GetRateAsync("EUR", "XYZ", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("ExchangeRate.UnknownCurrency");
    }

    [Fact]
    public async Task A_transport_failure_surfaces_as_a_Result_not_an_exception()
    {
        IExchangeRateProvider provider = BuildProvider(
            new StubHandler(_ => throw new HttpRequestException("connection refused")),
            maxRetries: 1);

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.Unavailable);
    }

    [Fact]
    public async Task An_unreachable_provider_is_Degraded_not_Unhealthy()
    {
        // A third party being down must NOT hard-fail our readiness — an orchestrator would pull a
        // perfectly serving instance out of rotation over someone else's outage. (The Unhealthy
        // branch is for a malformed BaseUrl, which ValidateOnStart now rejects before the check ever
        // runs; it stays in the check as defence in depth.) Port 1 refuses instantly.
        using ServiceProvider services = BuildServices(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            maxRetries: 1,
            baseUrl: "https://127.0.0.1:1/");

        var healthCheckService = services.GetRequiredService<HealthCheckService>();
        HealthReport report = await healthCheckService.CheckHealthAsync(
            registration => string.Equals(registration.Name, "exchange-rates", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        report.Entries["exchange-rates"].Status.ShouldBe(HealthStatus.Degraded);
    }

    private static IExchangeRateProvider BuildProvider(HttpMessageHandler handler, int maxRetries = 2)
        => BuildServices(handler, maxRetries).GetRequiredService<IExchangeRateProvider>();

    private static ServiceProvider BuildServices(
        HttpMessageHandler handler,
        int maxRetries = 2,
        string baseUrl = "https://rates.test/")
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ExternalApis:ExchangeRates:BaseUrl"] = baseUrl,
                ["ExternalApis:ExchangeRates:TimeoutSeconds"] = "5",
                ["ExternalApis:ExchangeRates:MaxRetries"] = maxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        // Swap the socket, keep everything else real.
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));

        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
