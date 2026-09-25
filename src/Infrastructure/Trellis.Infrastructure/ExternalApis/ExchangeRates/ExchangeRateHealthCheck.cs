using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Trellis.Infrastructure.ExternalApis.ExchangeRates;

/// <summary>
/// Readiness probe for the exchange-rate provider. Two deliberate design choices, both of which are
/// easy to get wrong:
/// <list type="number">
/// <item>
/// <b>It probes with a bare TCP connect, and caches the answer.</b> A health endpoint is usually the
/// most-polled route a service has; if each hit called the third party, an orchestrator (or anyone
/// who can reach <c>/health</c>) would have a free load lever against it — and against our rate
/// limit with that provider. One connect per <see cref="CacheWindow"/>, no application request.
/// </item>
/// <item>
/// <b>Unreachable is Degraded, misconfigured is Unhealthy.</b> A third party blipping must not
/// hard-fail our readiness and have the orchestrator pull a perfectly serving instance out of
/// rotation. A malformed BaseUrl is the opposite: that is our deployment being wrong, and it should
/// scream. (Normally unreachable — <see cref="ExchangeRateOptions"/> validates the URL at boot.)
/// </item>
/// </list>
/// Must be registered as a singleton, or the cache is rebuilt on every probe and buys nothing.
/// </summary>
internal sealed class ExchangeRateHealthCheck(IOptions<ExchangeRateOptions> options, TimeProvider timeProvider)
    : IHealthCheck
{
    private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly ExchangeRateOptions _options = options.Value;

    private HealthCheckResult? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        lock (_gate)
        {
            if (_cached is { } cached && now - _cachedAt < CacheWindow)
            {
                return cached;
            }
        }

        // Two concurrent misses may both probe. That is harmless (a bare TCP connect) and far
        // simpler than holding a lock across an await.
        HealthCheckResult result = await ProbeAsync(cancellationToken);

        lock (_gate)
        {
            _cached = result;
            _cachedAt = now;
        }

        return result;
    }

    private async Task<HealthCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            return HealthCheckResult.Unhealthy("ExternalApis:ExchangeRates:BaseUrl is missing or not an absolute URL.");
        }

        try
        {
            using var tcp = new TcpClient();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            await tcp.ConnectAsync(baseUri.Host, baseUri.Port, timeout.Token);

            return HealthCheckResult.Healthy($"Exchange-rate provider reachable at {baseUri.Host}:{baseUri.Port}.");
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return HealthCheckResult.Degraded(
                $"Exchange-rate provider not reachable at {baseUri.Host}:{baseUri.Port}.", exception);
        }
    }
}
