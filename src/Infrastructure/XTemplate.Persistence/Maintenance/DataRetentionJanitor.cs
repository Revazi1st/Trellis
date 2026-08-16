using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XTemplate.Persistence.Idempotency;
using XTemplate.Persistence.Identity;
using XTemplate.Persistence.Outbox;

namespace XTemplate.Persistence.Maintenance;

/// <summary>
/// Deletes expired bookkeeping rows on a timer: processed/dead-lettered outbox messages, stale
/// idempotency keys, and dead refresh tokens. Without it all three tables grow without bound.
/// Set-based ExecuteDelete — no entities are tracked. Safe to run on every replica.
/// </summary>
internal sealed class DataRetentionJanitor(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    RetentionOptions options,
    ILogger<DataRetentionJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Data-retention sweep failed");
            }

            try
            {
                await Task.Delay(options.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
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
