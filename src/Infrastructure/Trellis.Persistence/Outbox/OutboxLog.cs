using Microsoft.Extensions.Logging;

namespace Trellis.Persistence.Outbox;

/// <summary>
/// The outbox processor's log lines with stable event ids (1300–1399), so an alert matches on the id
/// instead of parsing a message. Levels follow the log-level policy (ADR-0023): a scheduled retry, a lost
/// lease, a failed handler and a batch that could not claim are Warnings — the processor recovers from
/// each by itself — and a dead letter is Critical, because nothing retries it.
/// </summary>
internal static partial class OutboxLog
{
    [LoggerMessage(EventId = 1301, EventName = "OutboxRetryScheduled", Level = LogLevel.Warning,
        Message = "Dispatch of outbox message {MessageId} ({Type}) failed (attempt {Attempts}/{MaxAttempts}); retrying at {NextAttemptAtUtc}")]
    public static partial void RetryScheduled(ILogger logger, Exception exception, Guid messageId, string type, int attempts, int maxAttempts, DateTimeOffset nextAttemptAtUtc);

    [LoggerMessage(EventId = 1302, EventName = "OutboxDeadLettered", Level = LogLevel.Critical,
        Message = "Outbox message {MessageId} ({Type}) dead-lettered after {Attempts} attempt(s): {Reason}. It will not retry — inspect the OutboxMessages table (re-drive runbook in docs/deployment.md)")]
    public static partial void DeadLettered(ILogger logger, Guid messageId, string type, int attempts, string reason);

    [LoggerMessage(EventId = 1303, EventName = "OutboxLeaseLost", Level = LogLevel.Warning,
        Message = "Outbox message {MessageId} ({Type}) is no longer this processor's: its lease (until {LeaseUntil}) was taken over, so this attempt's bookkeeping was discarded; the handlers it checkpointed stay credited")]
    public static partial void LeaseLost(ILogger logger, Guid messageId, string type, DateTimeOffset leaseUntil);

    [LoggerMessage(EventId = 1304, EventName = "OutboxHandlerFailed", Level = LogLevel.Warning,
        Message = "Handler {Handler} of outbox message {MessageId} ({Type}) failed; its sibling handlers still run and it alone is retried")]
    public static partial void HandlerFailed(ILogger logger, Exception exception, string handler, Guid messageId, string type);

    [LoggerMessage(EventId = 1305, EventName = "OutboxBookkeepingFailed", Level = LogLevel.Warning,
        Message = "Bookkeeping for outbox message {MessageId} ({Type}) failed; it stays leased until {LeaseUntil} and is dispatched again then")]
    public static partial void BookkeepingFailed(ILogger logger, Exception exception, Guid messageId, string type, DateTimeOffset leaseUntil);

    [LoggerMessage(EventId = 1306, EventName = "OutboxBatchStoppedShortOfLease", Level = LogLevel.Warning,
        Message = "Outbox batch stopped with {Remaining} message(s) unprocessed: the next one's dispatch budget would run past the lease ({LeaseUntil}); their leases are handed back and the next poll claims them")]
    public static partial void BatchStoppedShortOfLease(ILogger logger, int remaining, DateTimeOffset leaseUntil);

    [LoggerMessage(EventId = 1307, EventName = "OutboxLeasesReleased", Level = LogLevel.Information,
        Message = "Released the leases of {Released} outbox message(s) this batch ended before finishing")]
    public static partial void LeasesReleased(ILogger logger, int released);

    [LoggerMessage(EventId = 1308, EventName = "OutboxLeaseReleaseFailed", Level = LogLevel.Warning,
        Message = "Could not release {Count} outbox lease(s); they expire by themselves at {LeaseUntil}")]
    public static partial void LeaseReleaseFailed(ILogger logger, Exception exception, int count, DateTimeOffset leaseUntil);

    [LoggerMessage(EventId = 1309, EventName = "OutboxBatchFailed", Level = LogLevel.Warning,
        Message = "Outbox batch failed; polling again in {PollInterval}")]
    public static partial void BatchFailed(ILogger logger, Exception exception, TimeSpan pollInterval);
}
