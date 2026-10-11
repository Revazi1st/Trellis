using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Infrastructure;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// The reference outbound integration (ADR-0020), exercised through the real registration in
/// <c>AddOutboundIntegrations</c> — options binding, the typed client and the resilience pipeline all
/// included — with only the socket replaced. The contract under test is that nothing a third party
/// does reaches Application code as an exception: every outcome is a <see cref="Result{T}"/>, except
/// the caller's own cancellation, which must propagate.
/// </summary>
public sealed class ExchangeRateProviderTests
{
    /// <summary>The standard circuit breaker's default: it opens only once this many calls were sampled.</summary>
    private const int BreakerMinimumThroughput = 100;

    [Theory]
    // Frankfurter's actual /v2/rates body for base=EUR&quotes=USD, captured from the live API (rate edited).
    [InlineData("""[{"date":"2026-10-08","base":"EUR","quote":"USD","rate":1.0825}]""")]
    // More quotes than asked for: the one asked for is picked by name, not by position.
    [InlineData("""[{"date":"2026-10-08","base":"EUR","quote":"GBP","rate":0.8412},{"date":"2026-10-08","base":"EUR","quote":"USD","rate":1.0825}]""")]
    public async Task Provider_returns_the_quoted_rate(string body)
    {
        Uri? requested = null;
        IExchangeRateProvider provider = BuildProvider(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return Json(body);
        }));

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(1.0825m);
        // The v2 request shape, resolved beneath the configured BaseUrl.
        requested.ShouldBe(new Uri("https://rates.test/rates?base=EUR&quotes=USD"));
    }

    [Theory]
    // Frankfurter's live answer to an unknown code on either side (probed): a 422 naming one code.
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"status":422,"message":"invalid currency: XYZ"}""")]
    // A provider that rejects input with a plain 400 means the same for this endpoint.
    [InlineData(HttpStatusCode.BadRequest, """{"message":"bad request"}""")]
    public async Task Provider_maps_a_rejected_currency_pair_to_UnknownCurrency(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        IExchangeRateProvider provider = BuildProvider(handler);

        Result<decimal> result = await provider.GetRateAsync("EUR", "XYZ", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("ExchangeRate.UnknownCurrency");
        result.Error.Type.ShouldBe(ErrorType.Validation, "a code the provider does not know is the caller's input: a 400 at our API, not a 500");
        // The provider's text names one code without saying which side; the error names the pair in our words.
        result.Error.Description.ShouldContain("'EUR'");
        result.Error.Description.ShouldContain("'XYZ'");
        result.Error.Description.ShouldNotContain("invalid currency");
        handler.Calls.ShouldBe(1, "a rejected input is final, not retried");
    }

    [Theory]
    // Our credentials or plan, and a route the provider does not serve (a wrong BaseUrl): no wait cures them.
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    // A status this endpoint's contract does not list: our request no longer matches the provider's API.
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Provider_maps_a_refusal_of_our_configuration_to_Rejected_logged_at_Error(HttpStatusCode status)
    {
        var logs = new InProcess.AuthFlowTests.CapturingLoggerProvider();
        using ServiceProvider services = BuildServices(
            new StubHandler(_ => new HttpResponseMessage(status)),
            configure: s => s.AddSingleton<ILoggerProvider>(logs));

        Result<decimal> result = await services.GetRequiredService<IExchangeRateProvider>()
            .GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.Rejected);
        result.Error.Type.ShouldBe(ErrorType.Failure, "a 503 would tell every client to retry a fault only our configuration can fix");
        logs.All.ShouldContain(
            log => log.Level == LogLevel.Error && log.Category.EndsWith("ExchangeRateClient", StringComparison.Ordinal),
            "a refusal of our configuration must page someone, not read as an outage");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Provider_maps_a_transient_status_that_outlives_the_retries_to_Unavailable(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            // Immediate retries: this test is about the mapping, not the back-off.
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) },
        });
        IExchangeRateProvider provider = BuildProvider(handler, maxRetries: 1);

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.Unavailable);
        result.Error.Type.ShouldBe(ErrorType.Unavailable);
        handler.Calls.ShouldBe(2, "a transient status is retried before it is reported");
    }

    [Fact]
    public async Task Provider_matches_the_quote_whatever_case_the_caller_used()
    {
        // Probed: Frankfurter accepts "usd" and answers it as "USD".
        IExchangeRateProvider provider = BuildProvider(new StubHandler(_ => Json(
            """[{"date":"2026-10-11","base":"EUR","quote":"USD","rate":1.1217}]""")));

        Result<decimal> result = await provider.GetRateAsync("eur", "usd", TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(1.1217m);
    }

    [Fact]
    public async Task Provider_maps_unreadable_json_to_MalformedResponse()
    {
        IExchangeRateProvider provider = BuildProvider(new StubHandler(_ => Json("not json at all")));

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.MalformedResponse);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""[{"date":"2026-10-08","base":"EUR","quote":"USD","rate":1.08}]""")]
    public async Task Provider_reports_a_currency_the_feed_does_not_publish(string body)
    {
        IExchangeRateProvider provider = BuildProvider(new StubHandler(_ => Json(body)));

        Result<decimal> result = await provider.GetRateAsync("EUR", "XYZ", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("ExchangeRate.UnknownCurrency");
    }

    [Theory]
    // The v1 shape: an object where v2 answers an array.
    [InlineData("""{"amount":1.0,"base":"EUR","date":"2026-10-07","rates":{"USD":1.0825}}""")]
    // The quote is there but its rate is not.
    [InlineData("""[{"date":"2026-10-08","base":"EUR","quote":"USD"}]""")]
    public async Task Provider_maps_a_body_of_the_wrong_shape_to_MalformedResponse(string body)
    {
        IExchangeRateProvider provider = BuildProvider(new StubHandler(_ => Json(body)));

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.MalformedResponse);
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

        HealthReport report = await CheckExchangeRatesAsync(services);

        report.Entries["exchange-rates"].Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task GetRate_SuccessFalseAtHttp200_IsRejected()
    {
        // exchangerate.host's answer to every call without an access key: a 200 that says it failed.
        IExchangeRateProvider provider = BuildProvider(new StubHandler(_ => Json(
            """{"success":false,"error":{"code":101,"type":"missing_access_key","info":"You have not supplied an API Access Key."}}""")));

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.Rejected, "a missing key or an exhausted plan is ours to fix, not an outage to wait out");
    }

    [Fact]
    public async Task GetRate_ProviderSlowerThanTheAttemptTimeout_IsUnavailable()
    {
        // Every attempt hangs past the 1 s attempt timeout, so the pipeline gives up with Polly's
        // TimeoutRejectedException — not the TaskCanceledException a bare HttpClient would throw.
        var handler = StubHandler.Hanging();
        IExchangeRateProvider provider = BuildProvider(handler, maxRetries: 1, timeoutSeconds: 1);

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ExchangeRateErrors.Unavailable);
        handler.Calls.ShouldBe(2, "one attempt plus the one configured retry, each cut at the attempt timeout");
    }

    [Fact]
    public async Task GetRate_CircuitOpen_IsUnavailableAndSendsNothing()
    {
        // Enough concurrent failures to open the standard breaker, which samples at least 100 calls
        // before it judges. Every retry of the burst then meets the open circuit, which Polly reports
        // as BrokenCircuitException. Retry-After: 0 keeps those retries immediate, so the whole burst
        // lands well inside the breaker's sampling window and its 5 s break.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) },
        });
        using ServiceProvider services = BuildServices(handler, maxRetries: 1, timeoutSeconds: 1);
        var provider = services.GetRequiredService<IExchangeRateProvider>();
        CancellationToken ct = TestContext.Current.CancellationToken;

        Result<decimal>[] burst = await Task.WhenAll(
            Enumerable.Range(0, BreakerMinimumThroughput).Select(_ => provider.GetRateAsync("EUR", "USD", ct)));

        burst.ShouldAllBe(result => result.IsFailure && result.Error == ExchangeRateErrors.Unavailable);
        int sentBeforeTheCircuitOpened = handler.Calls;
        sentBeforeTheCircuitOpened.ShouldBeLessThan(BreakerMinimumThroughput * 2, "the open circuit must have refused some retries");

        Result<decimal> whileOpen = await provider.GetRateAsync("EUR", "USD", ct);

        whileOpen.IsFailure.ShouldBeTrue();
        whileOpen.Error.ShouldBe(ExchangeRateErrors.Unavailable);
        handler.Calls.ShouldBe(sentBeforeTheCircuitOpened, "an open circuit answers without sending");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetRate_TransientStatusThenOk_IsRetriedToSuccess(HttpStatusCode transient)
    {
        var handler = new StubHandler((_, call) => call == 1
            ? new HttpResponseMessage(transient)
            : Json("""[{"date":"2026-10-08","base":"EUR","quote":"USD","rate":1.0825}]"""));
        IExchangeRateProvider provider = BuildProvider(handler, maxRetries: 1);

        Result<decimal> result = await provider.GetRateAsync("EUR", "USD", TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(1.0825m);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task GetRate_CallerCancels_PropagatesOperationCanceledException()
    {
        // The one outcome the adapter must NOT convert: our own caller giving up. Turning it into a
        // failed Result would make an aborted request or a stopping host look like a provider outage.
        var handler = StubHandler.Hanging();
        IExchangeRateProvider provider = BuildProvider(handler, maxRetries: 2, timeoutSeconds: 30);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRateAsync("EUR", "USD", caller.Token));

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "cancellation must cut the attempt, not wait out its timeout");
        handler.Calls.ShouldBe(1, "a cancelled call is not retried");
    }

    [Fact]
    public async Task HealthCheck_WithinThirtySeconds_AnswersFromCacheWithoutProbing()
    {
        // A live listener makes the first probe Healthy; once it is gone, only a fresh probe can see
        // that. So Healthy at +29 s proves the cache answered, and Degraded at +31 s proves it expired.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clock = new OffsetClock();
        using ServiceProvider services = BuildServices(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            baseUrl: $"https://127.0.0.1:{port}/",
            configure: s => s.AddSingleton<TimeProvider>(clock));

        (await CheckExchangeRatesAsync(services)).Entries["exchange-rates"].Status.ShouldBe(HealthStatus.Healthy);

        listener.Stop();
        clock.Advance(TimeSpan.FromSeconds(29));
        (await CheckExchangeRatesAsync(services)).Entries["exchange-rates"].Status.ShouldBe(HealthStatus.Healthy);

        clock.Advance(TimeSpan.FromSeconds(2));
        (await CheckExchangeRatesAsync(services)).Entries["exchange-rates"].Status.ShouldBe(HealthStatus.Degraded);
    }

    private static Task<HealthReport> CheckExchangeRatesAsync(ServiceProvider services)
        => services.GetRequiredService<HealthCheckService>().CheckHealthAsync(
            static registration => string.Equals(registration.Name, "exchange-rates", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

    private static IExchangeRateProvider BuildProvider(HttpMessageHandler handler, int maxRetries = 2, int timeoutSeconds = 5)
        => BuildServices(handler, maxRetries, timeoutSeconds).GetRequiredService<IExchangeRateProvider>();

    private static ServiceProvider BuildServices(
        HttpMessageHandler handler,
        int maxRetries = 2,
        int timeoutSeconds = 5,
        string baseUrl = "https://rates.test/",
        Action<IServiceCollection>? configure = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ExternalApis:ExchangeRates:BaseUrl"] = baseUrl,
                ["ExternalApis:ExchangeRates:TimeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ExternalApis:ExchangeRates:MaxRetries"] = maxRetries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        services.AddOutboundIntegrations(configuration);
        // Swap the socket, keep everything else real.
        services.ConfigureHttpClientDefaults(builder => builder.ConfigurePrimaryHttpMessageHandler(() => handler));
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>System time plus an offset the test moves forward.</summary>
    private sealed class OffsetClock : TimeProvider
    {
        private long _offsetTicks;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));
    }
}

/// <summary>The socket stand-in for the exchange-rate tests: answers per call number and counts the calls it saw.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond)
    : HttpMessageHandler
{
    private int _calls;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _, _) => Task.FromResult(respond(request)))
    {
    }

    public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : this((request, call, _) => Task.FromResult(respond(request, call)))
    {
    }

    /// <summary>How many requests reached the socket — retries included, refused-by-the-pipeline excluded.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>A provider that never answers; only cancellation (an attempt timeout, the caller) ends a call.</summary>
    public static StubHandler Hanging() => new(static async (_, _, cancellationToken) =>
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new UnreachableException();
    });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => respond(request, Interlocked.Increment(ref _calls), cancellationToken);
}
