using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;
using Trellis.ServiceDefaults;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// What every host inherits from <c>AddServiceDefaults</c>, proven on a bare host with no database: the
/// prefix subscription that makes a new meter or trace source exported with no per-host registration,
/// the resource that names the emitter, and the blanket HttpClient retry that leaves unsafe methods alone.
/// Assertions read this host's own providers (a reader, a processor), never process-wide listeners, so
/// the in-process API hosts running in parallel cannot make them pass.
/// </summary>
public sealed class ServiceDefaultsTests
{
    private const string ApplicationName = "Trellis.ServiceDefaultsProbe";

    [Fact]
    public void A_meter_named_with_the_prefix_is_exported_and_an_unprefixed_one_is_not()
    {
        var exported = new CapturingMetricExporter();
        using IHost host = BuildHost(configureServices: services => services.ConfigureOpenTelemetryMeterProvider(
            metrics => metrics.AddReader(new BaseExportingMetricReader(exported))));
        MeterProvider provider = host.Services.GetRequiredService<MeterProvider>();

        string id = Guid.NewGuid().ToString("N");
        using var ours = new Meter($"{Extensions.TelemetryPrefix}.Probe{id}");
        using var polly = new Meter("Polly");
        using var efCore = new Meter("Microsoft.EntityFrameworkCore");
        using var unrelated = new Meter($"Unrelated.Probe{id}");
        foreach (Meter meter in new[] { ours, polly, efCore, unrelated })
        {
            meter.CreateCounter<long>($"probe.{id}", unit: "{probe}").Add(1);
        }

        provider.ForceFlush().ShouldBeTrue();

        exported.Saw(ours.Name).ShouldBeTrue(ours.Name);
        exported.Saw("Polly").ShouldBeTrue("Polly");
        exported.Saw("Microsoft.EntityFrameworkCore").ShouldBeTrue("Microsoft.EntityFrameworkCore");
        exported.Saw(unrelated.Name).ShouldBeFalse(unrelated.Name);
    }

    [Fact]
    public void A_trace_source_named_with_the_prefix_is_exported_and_an_unprefixed_one_is_not()
    {
        var exported = new CapturingActivityProcessor();
        using IHost host = BuildHost(configureServices: services => services.ConfigureOpenTelemetryTracerProvider(
            tracing => tracing.AddProcessor(exported)));
        host.Services.GetRequiredService<TracerProvider>();

        string id = Guid.NewGuid().ToString("N");
        using var ours = new ActivitySource($"{Extensions.TelemetryPrefix}.Probe{id}");
        using var unrelated = new ActivitySource($"Unrelated.Probe{id}");
        ours.StartActivity("probe")?.Dispose();
        unrelated.StartActivity("probe")?.Dispose();

        exported.Saw(ours.Name).ShouldBeTrue(ours.Name);
        exported.Saw(unrelated.Name).ShouldBeFalse(unrelated.Name);
    }

    [Fact]
    public void The_resource_names_the_application_its_version_and_its_environment()
    {
        using IHost host = BuildHost();

        Dictionary<string, object> resource = ResourceOf(host);

        resource["service.name"].ShouldBe(ApplicationName);
        resource["deployment.environment.name"].ShouldBe("Staging");
        resource["service.version"].ToString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void What_the_deployer_sets_through_OTEL_variables_wins()
    {
        using IHost host = BuildHost(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["OTEL_SERVICE_NAME"] = "billing-api",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "deployment.environment.name=prod-eu",
        });

        Dictionary<string, object> resource = ResourceOf(host);

        resource["service.name"].ShouldBe("billing-api");
        resource["deployment.environment.name"].ShouldBe("prod-eu");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task The_blanket_resilience_handler_never_retries_an_unsafe_method(string method)
    {
        var upstream = new AlwaysUnavailableHandler();
        using IHost host = BuildHost(configureServices: services =>
            services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => upstream));
        HttpClient client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");

        using var request = new HttpRequestMessage(new HttpMethod(method), "http://upstream.test/commands");
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        upstream.Calls.ShouldBe(1, $"a {method} whose response was lost may already have been applied");
    }

    [Fact]
    public async Task The_blanket_resilience_handler_still_retries_a_safe_method()
    {
        var upstream = new AlwaysUnavailableHandler();
        using IHost host = BuildHost(configureServices: services =>
            services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => upstream));
        HttpClient client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("probe");

        using HttpResponseMessage response = await client.GetAsync("http://upstream.test/queries", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        upstream.Calls.ShouldBeGreaterThan(1);
    }

    /// <summary>
    /// A bare host: no environment variables or appsettings (so a developer's shell cannot leak an
    /// <c>OTEL_*</c> variable in), named <see cref="ApplicationName"/>, in the Staging environment.
    /// </summary>
    private static IHost BuildHost(
        Dictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var settings = new ConfigurationManager();
        settings.AddInMemoryCollection(configuration ?? []);
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = ApplicationName,
            EnvironmentName = "Staging",
            DisableDefaults = true,
            Configuration = settings,
        });

        builder.AddServiceDefaults();
        configureServices?.Invoke(builder.Services);
        return builder.Build();
    }

    private static Dictionary<string, object> ResourceOf(IHost host)
        => host.Services.GetRequiredService<TracerProvider>().GetResource().Attributes
            .ToDictionary(static attribute => attribute.Key, static attribute => attribute.Value, StringComparer.Ordinal);

    private sealed class CapturingMetricExporter : BaseExporter<Metric>
    {
        private readonly ConcurrentDictionary<string, byte> _meterNames = new(StringComparer.Ordinal);

        public bool Saw(string meterName) => _meterNames.ContainsKey(meterName);

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch)
            {
                _meterNames.TryAdd(metric.MeterName, 0);
            }

            return ExportResult.Success;
        }
    }

    private sealed class CapturingActivityProcessor : BaseProcessor<Activity>
    {
        private readonly ConcurrentDictionary<string, byte> _sourceNames = new(StringComparer.Ordinal);

        public bool Saw(string sourceName) => _sourceNames.ContainsKey(sourceName);

        public override void OnEnd(Activity data) => _sourceNames.TryAdd(data.Source.Name, 0);
    }

    /// <summary>Answers every request 503 with <c>Retry-After: 0</c>, so a retry (if any) happens at once.</summary>
    private sealed class AlwaysUnavailableHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }
}
