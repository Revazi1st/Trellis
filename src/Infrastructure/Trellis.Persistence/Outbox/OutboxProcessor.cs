using System.Data.Common;
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
using Trellis.SharedKernel.Results;

namespace Trellis.Persistence.Outbox;

/// <summary>
/// Drains the transactional outbox (ADR-0018; processor semantics in ADR-0023). A poll claims due
/// messages in one short <c>FOR UPDATE SKIP LOCKED</c> transaction that only stamps a lease, then
/// dispatches each message outside any transaction, in its own DI scope, committing after every
/// handler. Failures back off exponentially; exhausted, unknown-type or unreadable messages are
/// dead-lettered loudly (log + metric), never dropped in silence.
/// </summary>
internal sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    OutboxOptions options,
    OutboxMetrics metrics,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private const string ProcessedOutcome = "processed";
    private const string RetryScheduledOutcome = "retry_scheduled";
    private const string DeadLetteredOutcome = "dead_lettered";

    private const string ExhaustedReason = "exhausted";
    private const string UnknownTypeReason = "unknown_type";
    private const string PoisonReason = "poison";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int claimed = 0;
            try
            {
                claimed = await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // Warning, not Error: the next poll retries the claim (log-level policy: a scheduled retry
                // is a Warning). A database that stays down shows as this line repeating every PollInterval
                // and as trellis.outbox.oldest_pending_age climbing.
                logger.LogWarning(exception, "Outbox batch failed; polling again in {PollInterval}", options.PollInterval);
            }

            // A full batch means more is probably due: claim again before sleeping. No hot loop is
            // possible — a failed claim counts as 0, failed messages are in back-off, leased ones are not
            // due and dead letters are not pending.
            if (claimed >= options.BatchSize)
            {
                continue;
            }

            try
            {
                await Task.Delay(options.PollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One poll: sample the backlog, claim and lease a batch, dispatch it. Returns how many messages were
    /// claimed so the loop knows whether to drain again. Internal for the tests.
    /// </summary>
    internal async Task<int> ProcessBatchAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset leaseUntil = now + options.LeaseDuration;
        List<OutboxMessage> batch;

        using (IServiceScope claimScope = scopeFactory.CreateScope())
        {
            AppDbContext context = claimScope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Before the claim, so a failed sample leaves nothing leased behind it.
            await SampleBacklogAsync(context, now, stoppingToken);
            batch = await ClaimAsync(context, now, leaseUntil, stoppingToken);
        }

        int finished = 0;
        try
        {
            foreach (OutboxMessage message in batch)
            {
                try
                {
                    await ProcessMessageAsync(message, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A bookkeeping save failed where nothing else catches it: the save of a failure (after a
                    // handler, a checkpoint or the Processed save failed) or of a dead letter — typically the
                    // database going away. That costs this message alone: its row still holds the lease and
                    // is claimed again when it expires, with whatever credits its checkpoints made durable.
                    // Letting it escape would strand the leases of every row after it as well.
                    logger.LogWarning(exception,
                        "Bookkeeping for outbox message {MessageId} ({Type}) failed; it stays leased until {LeaseUntil} and is dispatched again then",
                        message.Id, message.Type, leaseUntil);
                }

                finished++;
            }
        }
        finally
        {
            // Whatever ends the batch early — a stopping host, or anything the per-message catch does not
            // absorb — hands back the leases of the rows not reached first, best effort, so a rolling deploy
            // does not delay them by a whole lease. The exception then goes on to the caller.
            if (finished < batch.Count)
            {
                await ReleaseLeasesAsync(batch, leaseUntil);
            }
        }

        return batch.Count;
    }

    private async Task SampleBacklogAsync(AppDbContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Two trivial queries over the partial index; the gauges read what is recorded here, never the database.
        IQueryable<OutboxMessage> pending = context.Set<OutboxMessage>()
            .Where(static m => m.Status == OutboxMessageStatus.Pending);

        long count = await pending.LongCountAsync(cancellationToken);
        DateTimeOffset? oldest = count == 0
            ? null
            : await pending.MinAsync(static m => (DateTimeOffset?)m.OccurredAtUtc, cancellationToken);

        metrics.RecordBacklog(count, oldest is { } occurredAt ? now - occurredAt : TimeSpan.Zero);
    }

    private async Task<List<OutboxMessage>> ClaimAsync(AppDbContext context, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken stoppingToken)
    {
        // The retrying execution strategy forbids bare user transactions, so the claim runs inside it. The
        // delegate is pure database work (claim, lease stamp, commit) and nothing else: a transient failure
        // re-runs it harmlessly, whereas a handler inside it would run twice (ADR-0023).
        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            async cancellationToken =>
            {
                // A retried delegate must not carry the previous attempt's tracked rows into its save.
                context.ChangeTracker.Clear();
                await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                // FOR UPDATE SKIP LOCKED: a second replica claims different rows instead of the same ones.
                // No LINQ on top of this query (no OrderBy, no AsNoTracking): EF would wrap the locking SQL
                // in a subquery, and the rows must stay tracked so the lease stamp is a plain save.
                List<OutboxMessage> rows = await context.Set<OutboxMessage>()
                    .FromSql($"""
                        SELECT * FROM "OutboxMessages"
                        WHERE "Status" = 0 AND "NextAttemptAtUtc" <= {now}
                        ORDER BY "OccurredAtUtc"
                        LIMIT {options.BatchSize}
                        FOR UPDATE SKIP LOCKED
                        """)
                    .ToListAsync(cancellationToken);

                if (rows.Count == 0)
                {
                    return rows;
                }

                // The lease: a claimed row is "not due" to every other replica until this one finishes it
                // (Processed, back-off and dead-letter all overwrite NextAttemptAtUtc) or dies (the lease
                // expires by itself and the row is simply claimed again, credits intact).
                foreach (OutboxMessage row in rows)
                {
                    row.NextAttemptAtUtc = leaseUntil;
                }

                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return rows;
            },
            stoppingToken);
    }

    private async Task ProcessMessageAsync(OutboxMessage message, CancellationToken stoppingToken)
    {
        // The span starts before deserialization and rejoins the originating request's trace instead of
        // opening an orphan root: an unknown type or an unreadable payload shows up under the request that
        // produced it, not nowhere.
        using Activity? activity = OutboxMetrics.ActivitySource.StartActivity(
            $"outbox {message.Type}", ActivityKind.Internal, message.TraceParent);
        activity?.SetTag(OutboxMetrics.MessageIdAttribute, message.Id);
        activity?.SetTag(OutboxMetrics.EventTypeAttribute, message.Type);
        activity?.SetTag(OutboxMetrics.AttemptAttribute, message.Attempts + 1);
        using IDisposable? logScope = logger.BeginScope("OutboxMessage {MessageId} {EventType}", message.Id, message.Type);
        long started = timeProvider.GetTimestamp();

        // One scope per message — the isolation a request gets: a fresh DbContext whose tracker holds this
        // message and what its handlers write, nothing from its neighbours, and fresh scoped handlers.
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Attach(message);   // Unchanged snapshot: every bookkeeping save below is a single-row UPDATE of what changed.

        // Expected, permanent failures are decided without exceptions: an unknown wire name, or a payload
        // or CompletedHandlers column that does not parse. No retry can fix either, so the row is
        // dead-lettered at once with Attempts untouched, and the rest of the batch goes on.
        Result<Materialized> materialized = Materialize(message);
        string? outcome;
        if (materialized.IsFailure)
        {
            string reason = string.Equals(materialized.Error.Code, OutboxErrors.UnknownEventTypeCode, StringComparison.Ordinal)
                ? UnknownTypeReason
                : PoisonReason;
            DeadLetter(message, activity, materialized.Error.Description, reason);
            await context.SaveChangesAsync(CancellationToken.None);
            outcome = DeadLetteredOutcome;
        }
        else
        {
            outcome = await DispatchAsync(scope.ServiceProvider, context, message, materialized.Value, activity, stoppingToken);
        }

        // No outcome means the host stopped mid-dispatch: the caller hands the lease back, and an
        // interrupted dispatch is not a duration to measure.
        if (outcome is not null)
        {
            metrics.DispatchDuration(message.Type, timeProvider.GetElapsedTime(started), outcome);
        }
    }

    /// <summary>Runs the handlers under the budget and the originating tenant and books the outcome; null when the host stopped mid-dispatch.</summary>
    private async Task<string?> DispatchAsync(
        IServiceProvider services, AppDbContext context, OutboxMessage message, Materialized materialized, Activity? activity, CancellationToken stoppingToken)
    {
        (IDomainEvent domainEvent, HashSet<string> completed) = materialized;
        IDomainEventDispatcher dispatcher = services.GetRequiredService<IDomainEventDispatcher>();
#if (UseMultitenancy)
        // Handlers read AND write as the tenant that raised the event, not as "no tenant": the per-handler
        // saves below run inside this scope, so the TenantInterceptor stamps their rows with it. Disposed
        // on every exit, a stopping host's cancellation included.
        using var tenant = new AmbientTenantScope(message.TenantId);
#endif
        try
        {
            // The budget: handlers see a token that fires at DispatchTimeout. Because the catch below keys on
            // the host's token, a budget expiry is an ordinary retryable failure — and a cooperative handler
            // can never hold a message past its lease.
            using var budget = new CancellationTokenSource(options.DispatchTimeout, timeProvider);
            using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, budget.Token);

            await dispatcher.Dispatch(
                domainEvent,
                completed,
                checkpoint: handler =>
                {
                    // Commit this handler's tracked writes together with its credit, so "credited" always
                    // means "durable". CancellationToken.None: the side effect already happened, and a
                    // stopping host must still write the credit or the handler runs again on the next one.
                    message.CompletedHandlers = JsonSerializer.Serialize(completed.Append(handler));
                    return context.SaveChangesAsync(CancellationToken.None);
                },
                dispatch.Token);

            message.Status = OutboxMessageStatus.Processed;
            message.ProcessedAtUtc = timeProvider.GetUtcNow();
            message.Error = null;
            await context.SaveChangesAsync(CancellationToken.None);
            metrics.MessageDispatched(message.Type);
            activity?.SetTag(OutboxMetrics.OutcomeAttribute, ProcessedOutcome);
            return ProcessedOutcome;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Keyed on the host's token, never the linked one: a downstream timeout (an
            // OperationCanceledException too) and the budget above are failures to retry; only a stopping
            // host is not. Whatever a handler left in this scope's tracker was never committed (checkpoints
            // commit per handler): forget it, re-attach the message as modified and persist the failure alone.
            context.ChangeTracker.Clear();
            context.Update(message);
            string outcome = RecordFailure(message, activity, exception, completed);
            await context.SaveChangesAsync(CancellationToken.None);
            return outcome;
        }
    }

    /// <summary>Books one failed attempt: a back-off, or the dead letter once the attempts are exhausted. Returns the outcome tag.</summary>
    private string RecordFailure(OutboxMessage message, Activity? activity, Exception exception, HashSet<string> completed)
    {
        // Attempts counts claims that ended in failure — exactly one increment per claim, however many
        // handlers ran. Update() rewrites every column, so the whole row is made consistent: pending, not
        // processed, carrying exactly the handlers credited so far (their checkpoints are durable).
        message.Attempts++;
        message.Status = OutboxMessageStatus.Pending;
        message.ProcessedAtUtc = null;
        message.CompletedHandlers = completed.Count > 0 ? JsonSerializer.Serialize(completed) : null;
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.AddException(exception);

        if (message.Attempts >= options.MaxAttempts)
        {
            DeadLetter(message, activity, exception.Message, ExhaustedReason);
            return DeadLetteredOutcome;
        }

        message.Error = exception.Message;
        message.NextAttemptAtUtc = timeProvider.GetUtcNow() + OutboxBackoff.DelayAfter(options, message.Attempts);
        metrics.MessageRetried(message.Type, exception.GetType().Name);
        activity?.SetTag(OutboxMetrics.OutcomeAttribute, RetryScheduledOutcome);

        // Warning, not Error: the retry is scheduled and the trellis.outbox.retries counter is the alarm
        // (log-level policy); the exception rides along so the cause is one search away.
        logger.LogWarning(exception,
            "Dispatch of outbox message {MessageId} ({Type}) failed (attempt {Attempts}/{MaxAttempts}); retrying at {NextAttemptAtUtc}",
            message.Id, message.Type, message.Attempts, options.MaxAttempts, message.NextAttemptAtUtc);
        return RetryScheduledOutcome;
    }

    private void DeadLetter(OutboxMessage message, Activity? activity, string reason, string reasonTag)
    {
        message.Status = OutboxMessageStatus.DeadLettered;
        message.ProcessedAtUtc = null;
        message.Error = reason;
        metrics.MessageDeadLettered(message.Type, reasonTag);
        activity?.SetStatus(ActivityStatusCode.Error, reason);
        activity?.SetTag(OutboxMetrics.OutcomeAttribute, DeadLetteredOutcome);
        activity?.SetTag(OutboxMetrics.ReasonAttribute, reasonTag);
        if (logger.IsEnabled(LogLevel.Critical))
        {
            logger.LogCritical(
                "Outbox message {MessageId} ({Type}) dead-lettered after {Attempts} attempt(s): {Reason}. It will not retry — inspect the OutboxMessages table (re-drive runbook in docs/deployment.md)",
                message.Id, message.Type, message.Attempts, reason);
        }
    }

    /// <summary>
    /// Hands back the leases of the rows a batch that ended early did not finish — on a stopping host, the
    /// ones it never reached and the one in flight (its checkpointed credits are durable, so re-running it
    /// is safe). A row whose own bookkeeping failed is not among them: its in-memory state no longer
    /// carries the lease stamp, and it keeps the lease the database still holds. Best effort: a lease
    /// that cannot be released expires by itself.
    /// </summary>
    private async Task ReleaseLeasesAsync(List<OutboxMessage> batch, DateTimeOffset leaseUntil)
    {
        // Unfinished rows still carry the lease stamp in memory; finished ones had it overwritten.
        Guid[] unfinished = [.. batch
            .Where(m => m.Status == OutboxMessageStatus.Pending && m.NextAttemptAtUtc == leaseUntil)
            .Select(static m => m.Id)];
        if (unfinished.Length == 0)
        {
            return;
        }

        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DateTimeOffset now = timeProvider.GetUtcNow();

            // The same predicate again in SQL: only rows that still hold THIS lease are touched. Not the
            // stopping token — this is the bookkeeping of a batch already ending.
            int released = await context.Set<OutboxMessage>()
                .Where(m => unfinished.Contains(m.Id) && m.Status == OutboxMessageStatus.Pending && m.NextAttemptAtUtc == leaseUntil)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.NextAttemptAtUtc, now), CancellationToken.None);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Released the leases of {Released} outbox message(s) this batch ended before finishing", released);
            }
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or RetryLimitExceededException)
        {
            logger.LogWarning(exception,
                "Could not release {Count} outbox lease(s); they expire by themselves at {LeaseUntil}", unfinished.Length, leaseUntil);
        }
    }

    private static Result<Materialized> Materialize(OutboxMessage message)
    {
        try
        {
            IDomainEvent? domainEvent = DomainEventSerialization.Deserialize(message.Type, message.Content);
            if (domainEvent is null)
            {
                return OutboxErrors.UnknownEventType(message.Type);
            }

            HashSet<string> completed = message.CompletedHandlers is { Length: > 0 } json
                ? JsonSerializer.Deserialize<HashSet<string>>(json) ?? []
                : [];
            return new Materialized(domainEvent, completed);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or NotSupportedException or InvalidOperationException)
        {
            // Everything System.Text.Json and the strongly-typed-id converter throw for bytes that do not
            // parse. The bytes never change, so a retry would throw the same way: a permanent failure.
            return OutboxErrors.PoisonPayload(exception.Message);
        }
    }

    private readonly record struct Materialized(IDomainEvent DomainEvent, HashSet<string> CompletedHandlers);

#if (UseMultitenancy)
    /// <summary>The originating tenant, ambient for exactly one message's dispatch and its bookkeeping saves.</summary>
    private readonly struct AmbientTenantScope : IDisposable
    {
        public AmbientTenantScope(string? tenantId) => AmbientTenant.CurrentTenantId = tenantId;

        public void Dispose() => AmbientTenant.CurrentTenantId = null;
    }
#endif
}
