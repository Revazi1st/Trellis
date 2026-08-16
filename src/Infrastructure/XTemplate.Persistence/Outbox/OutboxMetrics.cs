using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace XTemplate.Persistence.Outbox;

/// <summary>
/// Outbox observability: dispatch/dead-letter counters and the ActivitySource whose spans attach to
/// the originating request's trace. Register the meter and source with OpenTelemetry in the host.
/// </summary>
public sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "XTemplate.Outbox";
    public const string ActivitySourceName = "XTemplate.Outbox";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly Meter _meter;
    private readonly Counter<long> _dispatched;
    private readonly Counter<long> _deadLettered;

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);
        _dispatched = _meter.CreateCounter<long>("xtemplate.outbox.dispatched", description: "Outbox messages dispatched successfully.");
        _deadLettered = _meter.CreateCounter<long>("xtemplate.outbox.dead_lettered", description: "Outbox messages abandoned after exhausting retries or failing to deserialize. Should be zero; alert on it.");
    }

    internal void MessageDispatched(string type) => _dispatched.Add(1, new KeyValuePair<string, object?>("event.type", type));

    internal void MessageDeadLettered(string type) => _deadLettered.Add(1, new KeyValuePair<string, object?>("event.type", type));

    public void Dispose() => _meter.Dispose();
}
