using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trellis.Persistence.Idempotency;
using Trellis.Persistence.Identity;
using Trellis.Persistence.Outbox;

namespace Trellis.Persistence.Maintenance;

/// <summary>
/// Deletes expired bookkeeping rows on a timer: processed/dead-lettered outbox messages, stale
/// idempotency keys, and dead refresh tokens. Without it all three tables grow without bound.
/// Set-based ExecuteDelete — no entities are tracked. Safe to run on every replica.
/// <para>
/// Pending outbox rows are never swept, however old: a pending row is an event whose handlers have
/// not all run yet, and deleting it would drop a side effect silently. Only the terminal states
/// (Processed, DeadLettered) age out. <c>DataRetentionJanitorTests</c> pins every predicate at its
/// cutoff, one second either side.
/// </para>
/// </summary>
internal sealed class DataRetentionJanitor(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    RetentionOptions options,
    ILogger<DataRetentionJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(options.InitialDelay, stoppingToken))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Warning, not Error: the next sweep is already scheduled and retries the same deletes
                // (log-level policy: a scheduled retry is a Warning). A sweep that keeps failing shows
                // up as this line repeating every SweepInterval.
                logger.LogWarning(exception, "Data-retention sweep failed; retrying in {SweepInterval}", options.SweepInterval);
            }

            if (!await DelayAsync(options.SweepInterval, stoppingToken))
            {
                return;
            }
        }
    }

    /// <summary>Waits on the injected clock (tests drive it with a fake); false when the host is stopping.</summary>
    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, timeProvider, stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTimeOffset now = timeProvider.GetUtcNow();

        DateTimeOffset processedCutoff = now - options.ProcessedOutboxAge;
        int outbox = await context.Set<OutboxMessage>()
            .Where(m => m.Status == OutboxMessageStatus.Processed && m.ProcessedAtUtc < processedCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        DateTimeOffset deadLetterCutoff = now - options.DeadLetteredOutboxAge;
        int deadLettered = await context.Set<OutboxMessage>()
            .Where(m => m.Status == OutboxMessageStatus.DeadLettered && m.OccurredAtUtc < deadLetterCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        DateTimeOffset idempotencyCutoff = now - options.IdempotencyKeyAge;
        int keys = await context.Set<IdempotencyKey>()
            .Where(k => k.CreatedAtUtc < idempotencyCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        DateTimeOffset tokenCutoff = now - options.DeadRefreshTokenAge;
        int tokens = await context.Set<RefreshToken>()
            .Where(t => (t.ConsumedAtUtc != null && t.ConsumedAtUtc < tokenCutoff)
                        || (t.ConsumedAtUtc == null && t.ExpiresAtUtc < tokenCutoff))
            .ExecuteDeleteAsync(cancellationToken);

        if (outbox + deadLettered + keys + tokens > 0 && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Retention sweep removed {Outbox} processed + {DeadLettered} dead-lettered outbox rows, {Keys} idempotency keys, {Tokens} refresh tokens",
                outbox, deadLettered, keys, tokens);
        }
    }
}
