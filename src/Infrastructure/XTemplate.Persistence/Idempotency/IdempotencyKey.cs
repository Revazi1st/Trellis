namespace XTemplate.Persistence.Idempotency;

/// <summary>A processed idempotency key; its primary key enforces at-most-once command processing.</summary>
internal sealed class IdempotencyKey
{
    public required string Key { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }
}
