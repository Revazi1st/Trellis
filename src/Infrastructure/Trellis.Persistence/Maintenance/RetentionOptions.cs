namespace Trellis.Persistence.Maintenance;

/// <summary>
/// Retention windows for the bookkeeping tables that would otherwise grow forever, bound from the
/// "Retention" configuration section. Dead-lettered outbox rows are kept longer than processed
/// ones — they are the alert queue.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan ProcessedOutboxAge { get; init; } = TimeSpan.FromDays(3);

    public TimeSpan DeadLetteredOutboxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Keys only guard against short-horizon retries; days of history buy nothing.</summary>
    public TimeSpan IdempotencyKeyAge { get; init; } = TimeSpan.FromDays(2);

    /// <summary>Consumed/expired refresh tokens, kept a while for incident forensics.</summary>
    public TimeSpan DeadRefreshTokenAge { get; init; } = TimeSpan.FromDays(30);
}
