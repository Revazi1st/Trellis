using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Trellis.Persistence.Outbox;

/// <summary>
/// Outbox observability: the dispatch counters and histogram, the backlog gauges, and the ActivitySource
/// whose spans attach to the originating request's trace. Every host exports it through ServiceDefaults'
/// <c>Trellis.*</c> subscription, no registration needed.
/// <para>
/// The two gauges report what the processor sampled on its last poll (<see cref="RecordBacklog"/>), never
/// a query from the gauge callback: the exporter's schedule stays independent of the database, and the
/// no-sync-over-async rule holds. Every replica reports the same global backlog — take <c>max</c>.
/// </para>
/// </summary>
public sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "Trellis.Outbox";
    public const string ActivitySourceName = "Trellis.Outbox";

    /// <summary>The event's wire name. Prefixed: <c>event.type</c> is a reserved semantic-convention namespace.</summary>
    public const string EventTypeAttribute = "trellis.outbox.event_type";

    /// <summary>The <c>OutboxMessages</c> row id, on the dispatch span.</summary>
    public const string MessageIdAttribute = "trellis.outbox.message_id";

    /// <summary>1-based attempt the span records.</summary>
    public const string AttemptAttribute = "trellis.outbox.attempt";

    /// <summary>
    /// <c>processed</c>, <c>retry_scheduled</c>, <c>dead_lettered</c> or <c>lease_lost</c> (the row was
    /// claimed by another processor while this one worked on it; nothing of this attempt was written).
    /// </summary>
    public const string OutcomeAttribute = "trellis.outbox.outcome";

    /// <summary>Why a message was dead-lettered: <c>exhausted</c>, <c>unknown_type</c> or <c>poison</c>.</summary>
    public const string ReasonAttribute = "trellis.outbox.reason";

    /// <summary>
    /// The semantic-convention attribute for what went wrong: on the span the failing exception's full
    /// type name, or the <c>Outbox.*</c> error code of an unreadable row; on the retries counter the
    /// exception's short type name (a low-cardinality dimension).
    /// </summary>
    public const string ErrorTypeAttribute = "error.type";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private readonly Meter _meter;
    private readonly Counter<long> _dispatched;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _retries;
    private readonly Histogram<double> _dispatchDuration;

    private long _pending;
    private double _oldestPendingAgeSeconds;

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);
        _dispatched = _meter.CreateCounter<long>(
            "trellis.outbox.dispatched", unit: "{message}",
            description: "Outbox messages whose handlers all succeeded.");
        _deadLettered = _meter.CreateCounter<long>(
            "trellis.outbox.dead_lettered", unit: "{message}",
            description: "Outbox messages abandoned: attempts exhausted, unknown event type or unreadable payload. Should be zero; alert on it.");
        _retries = _meter.CreateCounter<long>(
            "trellis.outbox.retries", unit: "{message}",
            description: "Failed dispatch attempts that scheduled a retry; a rising rate means a handler's dependency is struggling.");
        _dispatchDuration = _meter.CreateHistogram<double>(
            "trellis.outbox.dispatch.duration", unit: "s",
            description: "Wall time of one message's dispatch, from span start to its bookkeeping save.");
        _meter.CreateObservableGauge<long>(
            "trellis.outbox.pending", () => Volatile.Read(ref _pending), unit: "{message}",
            description: "Rows still pending (due, leased or in back-off) at the processor's last poll.");
        _meter.CreateObservableGauge<double>(
            "trellis.outbox.oldest_pending_age", () => Volatile.Read(ref _oldestPendingAgeSeconds), unit: "s",
            description: "Age of the oldest pending row at the processor's last poll; climbs when the drain is stalled.");
    }

    internal void MessageDispatched(string eventType)
        => _dispatched.Add(1, KeyValuePair.Create<string, object?>(EventTypeAttribute, eventType));

    internal void MessageRetried(string eventType, string errorType)
        => _retries.Add(1,
            KeyValuePair.Create<string, object?>(EventTypeAttribute, eventType),
            KeyValuePair.Create<string, object?>(ErrorTypeAttribute, errorType));

    internal void MessageDeadLettered(string eventType, string reason)
        => _deadLettered.Add(1,
            KeyValuePair.Create<string, object?>(EventTypeAttribute, eventType),
            KeyValuePair.Create<string, object?>(ReasonAttribute, reason));

    internal void DispatchDuration(string eventType, TimeSpan elapsed, string outcome)
        => _dispatchDuration.Record(elapsed.TotalSeconds,
            KeyValuePair.Create<string, object?>(EventTypeAttribute, eventType),
            KeyValuePair.Create<string, object?>(OutcomeAttribute, outcome));

    /// <summary>Called by the processor once per poll with what it counted; the gauges only read these.</summary>
    internal void RecordBacklog(long pending, TimeSpan oldestPendingAge)
    {
        Volatile.Write(ref _pending, pending);
        Volatile.Write(ref _oldestPendingAgeSeconds, oldestPendingAge.TotalSeconds);
    }

    public void Dispose() => _meter.Dispose();
}
