using System.ComponentModel.DataAnnotations;

namespace Trellis.Persistence.Maintenance;

/// <summary>
/// Retention windows for the bookkeeping tables that would otherwise grow forever, bound from the
/// "Retention" configuration section. Dead-lettered outbox rows are kept longer than processed
/// ones — they are the alert queue.
/// <para>
/// Every window carries a range, and the host that runs the janitor checks them at boot
/// (<c>AddOptions&lt;RetentionOptions&gt;().ValidateDataAnnotations().ValidateOnStart()</c> in the API's
/// <c>Program.cs</c>): a zero <see cref="SweepInterval"/> would hot-loop the sweep, a negative one
/// would stop the host, and a zero or negative window would delete rows that are still doing their
/// job (an idempotency key a client is about to retry with, a consumed refresh token whose replay
/// must still revoke its family).
/// </para>
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Time between two sweeps.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Retention:SweepInterval must be between {1} and {2}.")]
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Wait before the first sweep, so a booting replica does not add a table-wide DELETE to its
    /// startup work (and replicas started together do not all sweep at the same instant). Zero sweeps
    /// at once.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Retention:InitialDelay must be between {1} and {2}.")]
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(1);

    [Range(typeof(TimeSpan), "01:00:00", "365.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Retention:ProcessedOutboxAge must be between {1} and {2}.")]
    public TimeSpan ProcessedOutboxAge { get; init; } = TimeSpan.FromDays(3);

    /// <summary>Dead letters wait for a human; less than a day would delete them before anyone looked.</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Retention:DeadLetteredOutboxAge must be between {1} and {2}.")]
    public TimeSpan DeadLetteredOutboxAge { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Keys only guard against short-horizon retries; days of history buy nothing.</summary>
    [Range(typeof(TimeSpan), "01:00:00", "365.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Retention:IdempotencyKeyAge must be between {1} and {2}.")]
    public TimeSpan IdempotencyKeyAge { get; init; } = TimeSpan.FromDays(2);

    /// <summary>
    /// Consumed/expired refresh tokens, kept a while for incident forensics. A consumed token is also
    /// what makes a replay detectable (the replay revokes the whole family): keep this at least as long
    /// as <c>Authentication:RefreshTokenDays</c>.
    /// </summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Retention:DeadRefreshTokenAge must be between {1} and {2}.")]
    public TimeSpan DeadRefreshTokenAge { get; init; } = TimeSpan.FromDays(30);
}
