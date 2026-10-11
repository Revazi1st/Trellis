using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Infrastructure;
using Trellis.Infrastructure.ExternalApis.ExchangeRates;
using Trellis.ServiceDefaults;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// The exchange-rate client inside the graph the API really builds: <c>AddServiceDefaults()</c> (which
/// puts a standard resilience handler on every HttpClient), then <c>AddInfrastructure</c> and
/// <c>AddOutboundIntegrations</c>. The plain
/// <see cref="ExchangeRateProviderTests"/> graph has no ServiceDefaults, so it cannot see the client's
/// pipeline being stacked inside the blanket one — these tests exist for exactly that.
/// </summary>
public sealed class ExchangeRateResilienceTests
{
    private const string OkBody = """[{"date":"2026-10-08","base":"EUR","quote":"USD","rate":1.0825}]""";

    [Fact]
    public void HandlerChain_UnderServiceDefaults_HasExactlyOneResilienceHandler()
    {
        using IHost host = BuildApiShapedHost(new StubHandler(_ => Ok()));
        var handlers = host.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        // Control: ServiceDefaults is really in this graph — any other client carries its handler.
        ResilienceHandlersIn(handlers.CreateHandler("any-other-client")).ShouldBe(1);

        // The exchange-rate client replaced it instead of nesting its own inside it.
        ResilienceHandlersIn(handlers.CreateHandler(nameof(IExchangeRateProvider))).ShouldBe(1);
    }

    [Fact]
    public async Task GetRate_UnderServiceDefaults_ProviderHangs_IsUnavailableWithinTheClientsOwnBudget()
    {
        // 1 s attempts, one retry: the client's own budget is a few seconds. Stacked inside the blanket
        // handler, the call would instead run the client's retry loop once per outer attempt (calls
        // multiply) and take well past the blanket's 10 s attempt timeout.
        var handler = StubHandler.Hanging();
        using IHost host = BuildApiShapedHost(handler, timeoutSeconds: 1, maxRetries: 1);
        var provider = host.Services.GetRequiredService<IExchangeRateProvider>();
        var elapsed = Stopwatch.StartNew();

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        elapsed.Stop();
        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.Unavailable);
        handler.Calls.ShouldBe(2, "one attempt plus the one configured retry, not multiplied by the blanket handler's retries");
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "the client's 1 s attempt timeout must govern, not the blanket 10 s");
    }

    [Fact]
    public async Task GetRate_FailsOnceThenSucceeds_RetryIsNotCutOffByTheTotalTimeout()
    {
        // 1 s attempts, one retry. The provider answers 503 "come back in 4 s" and then the rate. The
        // 4 s wait fits only a budget that counts back-off: one counted in attempts alone (the former
        // attempt x (retries + 2) = 3 s) ends the call during the wait, before the retry ever runs.
        var handler = new StubHandler((_, call) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(4)) },
            }
            : Ok());
        using IHost host = BuildApiShapedHost(handler, timeoutSeconds: 1, maxRetries: 1);
        var provider = host.Services.GetRequiredService<IExchangeRateProvider>();
        var elapsed = Stopwatch.StartNew();

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        elapsed.Stop();
        result.ShouldBeSuccess();
        result.Value.ShouldBe(1.0825m);
        handler.Calls.ShouldBe(2);
        elapsed.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(3), "the retry must really have waited past an attempts-only budget");
    }

    [Fact]
    public async Task Boot_ApiShapedHost_RejectsAMalformedIntegrationSection()
    {
        using IHost host = BuildHost(callsOut: true, MalformedSection());

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));

        // Several validators can fail at once (ours and the resilience handler's), and the host then
        // reports them together; the integration's own options must be among them.
        IEnumerable<Exception> failures = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [failure];
        failures.OfType<OptionsValidationException>().ShouldContain(static e => e.OptionsType == typeof(ExchangeRateOptions));
    }

    [Fact]
    public async Task Boot_ApiShapedHost_WithoutAnIntegrationSection_StartsOnTheDefaults()
    {
        using IHost host = BuildHost(callsOut: true, new Dictionary<string, string?>(StringComparer.Ordinal));

        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested.ShouldBeTrue();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Boot_MigrationShapedHost_IgnoresAMalformedIntegrationSection()
    {
        // The migration service registers AddInfrastructure but never calls a third party, so it does
        // not register the outbound integrations — and must start whatever that section says. (Only
        // not validating it would not do: the standard resilience handler validates its own options
        // at boot, and the client's are computed from this section.)
        using IHost host = BuildHost(callsOut: false, MalformedSection());

        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested.ShouldBeTrue();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string?> MalformedSection() => new(StringComparer.Ordinal)
    {
        ["ExternalApis:ExchangeRates:BaseUrl"] = "not a url",
        ["ExternalApis:ExchangeRates:TimeoutSeconds"] = "0",
    };

    private static IHost BuildApiShapedHost(HttpMessageHandler socket, int timeoutSeconds = 5, int maxRetries = 2)
    {
        IHost host = BuildHost(
            callsOut: true,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ExternalApis:ExchangeRates:BaseUrl"] = "https://rates.test/",
                ["ExternalApis:ExchangeRates:TimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ExternalApis:ExchangeRates:MaxRetries"] = maxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            socket);
        return host;
    }

    /// <summary>
    /// The hosts' registration order — ServiceDefaults first, then Infrastructure, then (API only) the
    /// outbound integrations — on a host that reads only the settings given here (no appsettings, no
    /// environment variables).
    /// </summary>
    private static IHost BuildHost(
        bool callsOut,
        Dictionary<string, string?> settings,
        HttpMessageHandler? socket = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ApplicationName = typeof(ExchangeRateResilienceTests).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.AddInMemoryCollection(settings);

        builder.AddServiceDefaults();
        builder.Services.AddInfrastructure(builder.Configuration);
        if (callsOut)
        {
            builder.Services.AddOutboundIntegrations(builder.Configuration);
        }

        if (socket is not null)
        {
            // Swap the socket, keep every handler above it real.
            builder.Services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => socket));
        }

        return builder.Build();
    }

    private static int ResilienceHandlersIn(HttpMessageHandler outermost)
    {
        int count = 0;
        for (HttpMessageHandler? handler = outermost; handler is not null; handler = (handler as DelegatingHandler)?.InnerHandler)
        {
            if (handler is ResilienceHandler)
            {
                count++;
            }
        }

        return count;
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(OkBody, Encoding.UTF8, "application/json"),
    };
}
