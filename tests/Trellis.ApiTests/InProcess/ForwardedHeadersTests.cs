using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// "Trust never widens" (ADR-0021) in the configuration Aspire's publishers produce:
/// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> with no trusted proxy configured. The WebHost then
/// clears both trusted lists; without the API's restore, ForwardedHeadersMiddleware would honour
/// X-Forwarded-For from anyone and a caller could choose its own rate-limit partition.
/// <para>
/// The setting is supplied with <c>UseSetting("ForwardedHeaders_Enabled", "true")</c>, the host
/// configuration key that the environment variable maps to. It does reach the WebHost defaults inside
/// WebApplicationFactory (<see cref="The_environment_switch_reaches_the_WebHost_and_loopback_trust_is_restored"/>
/// proves it), so no process-wide environment variable is needed; one would leak into every other
/// fixture running in parallel.
/// </para>
/// Its own factory, because it exhausts an auth partition.
/// </summary>
public sealed class ForwardedHeadersTests(ForwardedHeadersTests.ForwardingEnabledApiFactory factory)
    : IClassFixture<ForwardedHeadersTests.ForwardingEnabledApiFactory>
{
    private const string RemoteAddressProbe = "/__test/remote-address";

    [Fact]
    public void The_environment_switch_reaches_the_WebHost_and_loopback_trust_is_restored()
    {
        ForwardedHeadersOptions options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        // Without the switch the framework default is KnownIPNetworks = [127.0.0.0/8]; an empty list
        // proves the WebHost's "forwarders enabled" setup ran and cleared it.
        options.KnownIPNetworks.ShouldBeEmpty();
        options.KnownProxies.ShouldBe([IPAddress.IPv6Loopback, IPAddress.Loopback], ignoreOrder: true);
    }

    [Fact]
    public void Restoring_loopback_trust_logs_a_warning_naming_the_configuration_keys()
    {
        // A host whose logger factory bypasses ILoggerProvider (Serilog without writeToProviders) writes
        // nothing here, not even its startup lines; the warning still reaches its own sinks.
        Assert.SkipUnless(factory.Logs.Count > 0,
            "This host's logging does not reach ILoggerProvider instances, so the warning cannot be captured in-process.");

        factory.Logs.ShouldContain(static line =>
            line.Contains("ASPNETCORE_FORWARDEDHEADERS_ENABLED", StringComparison.Ordinal)
            && line.Contains("ForwardedHeaders__KnownNetworks__0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_spoofed_X_Forwarded_For_leaves_the_remote_address_unchanged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClientFrom(IPAddress.Parse("198.51.100.20"));

        using var request = new HttpRequestMessage(HttpMethod.Get, RemoteAddressProbe);
        request.Headers.Add("X-Forwarded-For", "203.0.113.1");

        (await (await client.SendAsync(request, ct)).Content.ReadAsStringAsync(ct)).ShouldBe("198.51.100.20");
    }

    [Fact]
    public async Task A_spoofed_X_Forwarded_For_cannot_split_the_auth_rate_limit_partition()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClientFrom(IPAddress.Parse("198.51.100.30"));

        // Alternating the claimed address would give each value its own partition (6 + 6 calls, never
        // past 10) if the header were trusted. It is not, so all 12 share one and the 11th is throttled.
        var statuses = new List<HttpStatusCode>();
        for (int attempt = 0; attempt < 12; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = JsonContent.Create(new LoginRequest("nobody@nowhere.dev", "wrong-password")),
            };
            request.Headers.Add("X-Forwarded-For", attempt % 2 == 0 ? "203.0.113.1" : "203.0.113.2");
            statuses.Add((await client.SendAsync(request, ct)).StatusCode);
        }

        statuses.Take(10).ShouldAllBe(static status => status == HttpStatusCode.Unauthorized);
        statuses.Skip(10).ShouldAllBe(static status => status == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_loopback_proxy_is_still_trusted_and_forwarded_headers_are_applied_once()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient viaLocalProxy = factory.CreateClientFrom(IPAddress.IPv6Loopback);

        // Two hops, both claimed. One pass (ForwardLimit 1) takes the last entry: 127.0.0.1. A second
        // pass would trust that loopback hop too and take the client-written 198.51.100.7.
        using var request = new HttpRequestMessage(HttpMethod.Get, RemoteAddressProbe);
        request.Headers.Add("X-Forwarded-For", "198.51.100.7, 127.0.0.1");

        (await (await viaLocalProxy.SendAsync(request, ct)).Content.ReadAsStringAsync(ct)).ShouldBe("127.0.0.1");
    }

    /// <summary>The API host with <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> and nothing else configured.</summary>
    public sealed class ForwardingEnabledApiFactory : ApiFactory
    {
        private readonly CapturingLoggerProvider _logs = new();

        public IReadOnlyCollection<string> Logs => _logs.Lines;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("ForwardedHeaders_Enabled", "true");
            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
            builder.ConfigureServices(static services =>
                services.AddTransient<IStartupFilter, RemoteAddressProbeStartupFilter>());
        }
    }

    /// <summary>
    /// Answers <see cref="RemoteAddressProbe"/> with the remote address as the application finally sees
    /// it. Mapped after the whole application pipeline, so every forwarded-headers pass has already run.
    /// </summary>
    private sealed class RemoteAddressProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Map(RemoteAddressProbe, static probe => probe.Run(static context =>
                context.Response.WriteAsync(context.Connection.RemoteIpAddress?.ToString() ?? "(none)")));
        };
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            // Every level: the startup lines are what tells a host that routes to ILoggerProvider from one that does not.
            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    lines.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
