#if (IncludeUI)
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Trellis.Api.Contracts.Invoices;
using Trellis.UI.Common.Services;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// <c>ApiCall</c>: every way a call can come back without a usable answer is an <see cref="ApiResult{T}"/>
/// failure carrying the service-unavailable sentence and no status, never an exception for the page's
/// error boundary — whichever typed-client shape made the call (a JSON body, a selected member of one,
/// the status alone). Only a cancellation the caller asked for propagates.
/// </summary>
public sealed class ApiCallTests
{
    private const string ServiceUnavailable = "The service is unavailable right now. Try again shortly.";

    /// <summary>The three call shapes the typed clients have, each reduced to (error, status).</summary>
    private static readonly string[] Shapes = ["json body", "selected member", "status only"];

    public static TheoryData<string> CallShapes() => [.. Shapes];

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public async Task A_call_that_got_no_answer_is_service_unavailable(string failure, string shape)
    {
        await using var ui = new UiHarness();
        ui.Api.Respond = (_, _) => throw TransportFailure(failure);

        (string? error, int? status) = await CallAsync(ui, shape, TestContext.Current.CancellationToken);

        error.ShouldBe(ServiceUnavailable);
        status.ShouldBeNull();
    }

    public static TheoryData<string, string> TransportFailures()
    {
        var data = new TheoryData<string, string>();
        foreach (string failure in new[] { "connection refused", "circuit open", "circuit isolated", "resilience timeout" })
        {
            foreach (string shape in Shapes)
            {
                data.Add(failure, shape);
            }
        }

        return data;
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null literal")]
    [InlineData("malformed json")]
    [InlineData("html page")]
    [InlineData("no content")]
    public async Task A_success_without_the_promised_body_is_service_unavailable(string body)
    {
        await using var ui = new UiHarness();
        ui.Api.Always(_ => SuccessWith(body));

        ApiResult<InvoiceDto> read = await ui.InvoiceApi.GetAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        ApiResult<Guid> created = await ui.InvoiceApi.CreateAsync(
            new CreateInvoiceRequest("Acme", "billing@acme.test", "USD"), TestContext.Current.CancellationToken);

        foreach ((bool isSuccess, string? error, int? status) in new[]
                 {
                     (read.IsSuccess, read.Error, read.StatusCode),
                     (created.IsSuccess, created.Error, created.StatusCode),
                 })
        {
            isSuccess.ShouldBeFalse();
            error.ShouldBe(ServiceUnavailable);
            status.ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_status_only_call_ignores_the_body_it_has_no_use_for()
    {
        await using var ui = new UiHarness();
        ui.Api.Always(static _ => SuccessWith("html page"));

        (await ui.InvoiceApi.PayAsync(Guid.CreateVersion7(), cancellationToken: TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(CallShapes))]
    public async Task The_clients_own_timeout_is_service_unavailable(string shape)
    {
        await using var ui = new UiHarness(clientTimeout: TimeSpan.FromMilliseconds(100));
        ui.Api.Respond = Hang;

        (string? error, int? status) = await CallAsync(ui, shape, TestContext.Current.CancellationToken);

        error.ShouldBe(ServiceUnavailable);
        status.ShouldBeNull();
    }

    [Fact]
    public async Task The_host_resilience_pipeline_timing_out_is_service_unavailable()
    {
        // The real standard handler a host adds (ServiceDefaults), with its timeouts shrunk: its total
        // request timeout rejects the call with Polly's TimeoutRejectedException.
        await using var ui = new UiHarness(configureServices: static services =>
            services.ConfigureHttpClientDefaults(static http => http.AddStandardResilienceHandler(static resilience =>
            {
                resilience.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(50);
                resilience.TotalRequestTimeout.Timeout = TimeSpan.FromMilliseconds(150);
                resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(1);
                resilience.Retry.DisableForUnsafeHttpMethods();
            })));
        ui.Api.Respond = Hang;

        ApiResult<InvoiceDto> read = await ui.InvoiceApi.GetAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        ApiResult<bool> paid = await ui.InvoiceApi.PayAsync(Guid.CreateVersion7(), cancellationToken: TestContext.Current.CancellationToken);

        read.Error.ShouldBe(ServiceUnavailable);
        paid.Error.ShouldBe(ServiceUnavailable);
    }

    [Theory]
    [MemberData(nameof(CallShapes))]
    public async Task A_cancellation_the_caller_asked_for_propagates(string shape)
    {
        await using var ui = new UiHarness();
        using var caller = new CancellationTokenSource();
        ui.Api.Respond = async (_, cancellationToken) =>
        {
            await caller.CancelAsync();
            return await Hang(null!, cancellationToken);
        };

        await Should.ThrowAsync<OperationCanceledException>(() => CallAsync(ui, shape, caller.Token));
    }

    [Fact]
    public async Task An_already_cancelled_caller_gets_its_cancellation_back()
    {
        await using var ui = new UiHarness();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => ui.AuthApi.LoginAsync(
            new LoginRequest("someone@example.com", "password"), cancelled.Token));
    }

    [Fact]
    public async Task A_bug_is_not_dressed_up_as_an_outage()
    {
        await using var ui = new UiHarness();
        ui.Api.Respond = static (_, _) => throw new InvalidOperationException("a programming error");

        await Should.ThrowAsync<InvalidOperationException>(() => ui.InvoiceApi.GetAsync(
            Guid.CreateVersion7(), TestContext.Current.CancellationToken));
    }

    private static async Task<(string? Error, int? Status)> CallAsync(UiHarness ui, string shape, CancellationToken cancellationToken)
    {
        switch (shape)
        {
            case "json body":
                ApiResult<TokenResponse> login = await ui.AuthApi.LoginAsync(
                    new LoginRequest("someone@example.com", "password"), cancellationToken);
                login.IsSuccess.ShouldBeFalse();
                return (login.Error, login.StatusCode);
            case "selected member":
                ApiResult<Guid> created = await ui.InvoiceApi.CreateAsync(
                    new CreateInvoiceRequest("Acme", "billing@acme.test", "USD"), cancellationToken);
                created.IsSuccess.ShouldBeFalse();
                return (created.Error, created.StatusCode);
            case "status only":
                ApiResult<bool> paid = await ui.InvoiceApi.PayAsync(Guid.CreateVersion7(), cancellationToken: cancellationToken);
                paid.IsSuccess.ShouldBeFalse();
                return (paid.Error, paid.StatusCode);
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown call shape.");
        }
    }

    private static Exception TransportFailure(string failure) => failure switch
    {
        "connection refused" => new HttpRequestException("Connection refused"),
        "circuit open" => new BrokenCircuitException("The circuit is now open and is not allowing calls."),
        "circuit isolated" => new IsolatedCircuitException("The circuit is manually held open."),
        "resilience timeout" => new TimeoutRejectedException("The operation didn't complete within the allowed timeout."),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown failure."),
    };

    private static HttpResponseMessage SuccessWith(string body) => body switch
    {
        "empty" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
        },
        "null literal" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        },
        "malformed json" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        },
        "html page" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>a captive portal</html>", Encoding.UTF8, "text/html"),
        },
        "no content" => new HttpResponseMessage(HttpStatusCode.NoContent),
        _ => throw new ArgumentOutOfRangeException(nameof(body), body, "Unknown body."),
    };

    private static async Task<HttpResponseMessage> Hang(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return Responses.Status(HttpStatusCode.OK);
    }
}
#endif
