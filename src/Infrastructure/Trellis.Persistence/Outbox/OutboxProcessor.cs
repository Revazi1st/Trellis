using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
#if (UseMultitenancy)
using Trellis.Application.Abstractions;
#endif
using Trellis.SharedKernel.Domain;

namespace Trellis.Persistence.Outbox;

/// <summary>
/// Drains the transactional outbox: claims due messages (skip-locked, so replicas never
/// double-dispatch), deserializes each domain event, and dispatches it to its handlers after the
/// originating transaction has committed. Failures back off exponentially; exhausted or
/// unresolvable messages are dead-lettered loudly (log + metric), never dropped in silence.
/// </summary>
internal sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    OutboxMetrics metrics,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromSeconds(30);
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
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
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

        // The retrying execution strategy forbids bare user transactions, so the claim runs inside it.
        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await context.Database.BeginTransactionAsync(cancellationToken);

            DateTimeOffset now = timeProvider.GetUtcNow();

            // FOR UPDATE SKIP LOCKED: a second replica claims different rows instead of the same ones.
            List<OutboxMessage> batch = await context.Set<OutboxMessage>()
                .FromSql($"""
                    SELECT * FROM "OutboxMessages"
                    WHERE "Status" = 0 AND "NextAttemptAtUtc" <= {now}
                    ORDER BY "OccurredAtUtc"
                    LIMIT {BatchSize}
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                return;
            }

            foreach (OutboxMessage message in batch)
            {
                await ProcessMessageAsync(message, dispatcher, cancellationToken);
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    private async Task ProcessMessageAsync(OutboxMessage message, IDomainEventDispatcher dispatcher, CancellationToken cancellationToken)
    {
        IDomainEvent? domainEvent = DomainEventSerialization.Deserialize(message.Type, message.Content);

        if (domainEvent is null)
        {
            DeadLetter(message, $"Unknown event type '{message.Type}'");
            return;
        }

        HashSet<string> completedHandlers = message.CompletedHandlers is { Length: > 0 } completedJson
            ? JsonSerializer.Deserialize<HashSet<string>>(completedJson) ?? []
            : [];

        // Rejoin the originating request's trace instead of starting an orphan root span.
        using Activity? activity = OutboxMetrics.ActivitySource.StartActivity(
            $"outbox {message.Type}", ActivityKind.Internal, message.TraceParent);

#if (UseMultitenancy)
        // Handlers must read and write as the tenant that raised the event, not as "no tenant".
        AmbientTenant.CurrentTenantId = message.TenantId;
#endif
        try
        {
            await dispatcher.Dispatch(domainEvent, completedHandlers, cancellationToken);
            message.Status = OutboxMessageStatus.Processed;
            message.ProcessedAtUtc = timeProvider.GetUtcNow();
            message.Error = null;
            metrics.MessageDispatched(message.Type);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            message.Attempts++;
            if (message.Attempts >= MaxAttempts)
            {
                DeadLetter(message, exception.Message);
            }
            else
            {
                // 30s → 1m → 2m → 4m: a downstream outage gets a real recovery window instead of
                // five retries burning out in seconds.
                message.Error = exception.Message;
                message.NextAttemptAtUtc = timeProvider.GetUtcNow() + BaseRetryDelay * Math.Pow(2, message.Attempts - 1);
                logger.LogError(exception,
                    "Dispatch of outbox message {MessageId} ({Type}) failed (attempt {Attempts}/{MaxAttempts}); retrying at {NextAttemptAtUtc}",
                    message.Id, message.Type, message.Attempts, MaxAttempts, message.NextAttemptAtUtc);
            }
        }
        finally
        {
#if (UseMultitenancy)
            AmbientTenant.CurrentTenantId = null;
#endif
            if (completedHandlers.Count > 0)
            {
                message.CompletedHandlers = JsonSerializer.Serialize(completedHandlers);
            }
        }
    }

    private void DeadLetter(OutboxMessage message, string reason)
    {
        message.Status = OutboxMessageStatus.DeadLettered;
        message.Error = reason;
        metrics.MessageDeadLettered(message.Type);
        if (logger.IsEnabled(LogLevel.Critical))
        {
            logger.LogCritical(
                "Outbox message {MessageId} ({Type}) dead-lettered after {Attempts} attempt(s): {Reason}. It will not retry — inspect the OutboxMessages table",
                message.Id, message.Type, message.Attempts, reason);
        }
    }
}
