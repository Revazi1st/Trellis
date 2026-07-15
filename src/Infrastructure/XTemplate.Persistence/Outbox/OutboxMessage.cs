namespace XTemplate.Persistence.Outbox;

/// <summary>
/// A domain event persisted in the same transaction as the aggregate change (transactional outbox).
/// A background processor deserializes and dispatches it after commit, so side-effecting handlers
/// never run on a rolled-back or retried transaction.
/// </summary>
internal sealed class OutboxMessage
{
    public Guid Id { get; init; }

    /// <summary>The event's CLR type full name, used to resolve the concrete type for deserialization.</summary>
    public required string Type { get; init; }

    /// <summary>The JSON-serialized event payload.</summary>
    public required string Content { get; init; }

    public DateTimeOffset OccurredAtUtc { get; init; }

    public DateTimeOffset? ProcessedAtUtc { get; set; }

    public int Attempts { get; set; }

    public string? Error { get; set; }
}
