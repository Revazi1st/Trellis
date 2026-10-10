namespace Trellis.Persistence.Outbox;

/// <summary>
/// A domain event persisted in the same transaction as the aggregate change (transactional outbox).
/// A background processor deserializes and dispatches it after commit, so side-effecting handlers
/// never run on a rolled-back or retried transaction.
/// </summary>
internal sealed class OutboxMessage
{
    public Guid Id { get; init; }

    /// <summary>The event's stable wire name (<see cref="Trellis.SharedKernel.Domain.DomainEventNameAttribute"/>), never the CLR type.</summary>
    public required string Type { get; init; }

    /// <summary>The JSON-serialized event payload.</summary>
    public required string Content { get; init; }

    public OutboxMessageStatus Status { get; set; }

    public DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>
    /// Not eligible for dispatch before this instant: the exponential back-off between attempts, or the
    /// in-flight lease while a processor holds the row (ADR-0023). A claim stamps
    /// <c>now + OutboxOptions.LeaseDuration</c> here, so every other processor sees the row as "not due"
    /// until this one finishes it or dies — same column, same partial index, no extra state.
    /// </summary>
    public DateTimeOffset NextAttemptAtUtc { get; set; }

    public DateTimeOffset? ProcessedAtUtc { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// JSON array of handler type names that already succeeded for this message, so a retry after a
    /// partial failure never re-runs them (no double email, no double-counted metric).
    /// </summary>
    public string? CompletedHandlers { get; set; }

    /// <summary>W3C traceparent of the originating operation — dispatch spans attach to the real trace.</summary>
    public string? TraceParent { get; init; }

#if (UseMultitenancy)
    /// <summary>The originating tenant, restored ambiently while the message's handlers run.</summary>
    public string? TenantId { get; init; }
#endif
}

internal enum OutboxMessageStatus
{
    Pending = 0,
    Processed = 1,

    /// <summary>Exhausted its attempts or its type is unresolvable; kept for inspection, never retried automatically.</summary>
    DeadLettered = 2,
}
