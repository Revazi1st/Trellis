using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Persistence.Outbox;

/// <summary>
/// Drains the transactional outbox: polls for unprocessed messages, deserializes each domain event,
/// and dispatches it to its handlers after the originating transaction has committed.
/// </summary>
internal sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 20;
    private const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Outbox processing batch failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IDomainEventDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDomainEventDispatcher>();

        List<OutboxMessage> batch = await context.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null && m.Attempts < MaxAttempts)
            .OrderBy(m => m.OccurredAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return;
        }

        foreach (OutboxMessage message in batch)
        {
            IDomainEvent? domainEvent = DomainEventSerialization.Deserialize(message.Type, message.Content);

            if (domainEvent is null)
            {
                message.Error = $"Unknown event type '{message.Type}'";
                message.Attempts = MaxAttempts; // don't retry an unresolvable type
                continue;
            }

            try
            {
                await dispatcher.Dispatch([domainEvent], cancellationToken);
                message.ProcessedAtUtc = timeProvider.GetUtcNow();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.Attempts++;
                message.Error = exception.Message;
                logger.LogError(exception, "Dispatch of outbox message {MessageId} ({Type}) failed (attempt {Attempts})",
                    message.Id, message.Type, message.Attempts);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
