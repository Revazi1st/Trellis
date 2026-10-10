using System.ComponentModel.DataAnnotations;

namespace Trellis.Persistence.Outbox;

/// <summary>
/// The outbox processor's knobs, bound from the "Outbox" configuration section. Every limit sits next to
/// its property as a DataAnnotation and the host that runs the processor checks them at boot
/// (<c>AddOptions&lt;OutboxOptions&gt;().ValidateDataAnnotations().ValidateOnStart()</c> in the API's
/// <c>Program.cs</c>, the pattern <c>RetentionOptions</c> set): a zero <see cref="PollInterval"/> would
/// hot-loop the drain, a zero <see cref="BatchSize"/> would claim nothing forever, and a zero
/// <see cref="MaxAttempts"/> would dead-letter every message on its first failure.
/// <para>
/// With the defaults a message that keeps failing is retried after 30 s, 1 m, 2 m, 4 m, 8 m, 15 m and
/// 15 m, and dead-lettered on its eighth failure, about 45 minutes after the first — a real downstream
/// outage window. <see cref="LeaseDuration"/> follows from the knobs and is not one.
/// </para>
/// </summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Sleep between two polls once a batch came back short; a full batch is followed by another claim at once.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Outbox:PollInterval must be between {1} and {2}.")]
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Messages claimed per poll — the LIMIT of the <c>FOR UPDATE SKIP LOCKED</c> claim.</summary>
    [Range(1, 1000, ErrorMessage = "Outbox:BatchSize must be between {1} and {2}.")]
    public int BatchSize { get; init; } = 20;

    /// <summary>Failed attempts before a message is dead-lettered and waits for a human (see the re-drive runbook in deployment.md).</summary>
    [Range(1, 50, ErrorMessage = "Outbox:MaxAttempts must be between {1} and {2}.")]
    public int MaxAttempts { get; init; } = 8;

    /// <summary>Wait after the first failure; it doubles with every further failure up to <see cref="MaxRetryDelay"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Outbox:BaseRetryDelay must be between {1} and {2}.")]
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Cap on the doubling, so a late attempt does not wait half an hour. At least <see cref="BaseRetryDelay"/> (checked where the section is bound).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Outbox:MaxRetryDelay must be between {1} and {2}.")]
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Budget for one message's handlers. A handler still running when it elapses sees its token cancelled,
    /// and the message is retried like any other failure — which is what makes the lease below safe: a
    /// cooperative handler cannot hold a message past it. Matches the 30 s total timeout of the standard
    /// resilience handler a handler's <c>HttpClient</c> already has.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true,
        ErrorMessage = "Outbox:DispatchTimeout must be between {1} and {2}.")]
    public TimeSpan DispatchTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a claimed message stays invisible to every other processor: the longest a batch's handlers
    /// can legitimately take (every message spending its whole <see cref="DispatchTimeout"/>) plus one slot
    /// for bookkeeping — 10.5 minutes with the defaults. The budget bounds the handlers, not the database:
    /// a lease expires under a process that crashed or hung, and also under one whose bookkeeping saves are
    /// slow (a struggling database, each save retried by the execution strategy) — another processor may
    /// then claim a row this one has not finished, which is why delivery stays at-least-once. A graceful
    /// stop releases its leases itself. After a crash, the rows it held reappear within this long: shorten
    /// <see cref="BatchSize"/> or <see cref="DispatchTimeout"/> to shorten it.
    /// </summary>
    public TimeSpan LeaseDuration => DispatchTimeout * (BatchSize + 1);
}
