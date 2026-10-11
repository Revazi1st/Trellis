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
/// handler. The lease is also the fence: every bookkeeping write is conditional on it, so a processor
/// that outlived its lease cannot overwrite what the next claimant did. Failures back off exponentially;
/// exhausted, unknown-type or unreadable messages are dead-lettered loudly (log + metric), never dropped
/// in silence.
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
    private const string LeaseLostOutcome = "lease_lost";

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
                OutboxLog.BatchFailed(logger, exception, options.PollInterval);
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
                // The lease is the fence: a message whose whole budget no longer fits before it would run
                // its handlers only to lose their bookkeeping to the next claimant. The rest of the batch
                // is handed back instead (the finally below), due again at once. Only a database slower
                // than the budget gets here — the lease is sized for every message spending its budget.
                if (timeProvider.GetUtcNow() + options.DispatchTimeout > leaseUntil)
                {
                    OutboxLog.BatchStoppedShortOfLease(logger, batch.Count - finished, leaseUntil);
                    break;
                }

                try
                {
                    await ProcessMessageAsync(message, leaseUntil, stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A bookkeeping save failed where nothing else catches it: the save of a failure (after a
                    // handler, a checkpoint or the Processed save failed) or of a dead letter — typically the
                    // database going away. That costs this message alone: its row still holds the lease and
                    // is claimed again when it expires, with whatever credits its checkpoints made durable.
                    // Letting it escape would strand the leases of every row after it as well.
                    OutboxLog.BookkeepingFailed(logger, exception, message.Id, message.Type, leaseUntil);
                }

                finished++;
            }
        }
        finally
        {
            // Whatever ends the batch early — a stopping host, the lease running short, or anything the
            // per-message catch does not absorb — hands back the leases of the rows not reached first,
            // best effort, so a rolling deploy does not delay them by a whole lease. An exception then
            // goes on to the caller.
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
                // in a subquery, and the rows must stay tracked so the lease stamp is a plain save. "Id"
                // (a time-ordered v7 GUID) is the final key, so two rows written in the same microsecond
                // are still claimed in one order on every poll and every replica, not in page order.
                List<OutboxMessage> rows = await context.Set<OutboxMessage>()
                    .FromSql($"""
                        SELECT * FROM "OutboxMessages"
                        WHERE "Status" = 0 AND "NextAttemptAtUtc" <= {now}
                        ORDER BY "OccurredAtUtc", "Id"
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
                // expires by itself and the row is simply claimed again, credits intact). Being the
                // concurrency token too, the value stamped here is what every later save of the row is
                // conditional on — the rows are locked, so this one save cannot find it changed.
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

    private async Task ProcessMessageAsync(OutboxMessage message, DateTimeOffset leaseUntil, CancellationToken stoppingToken)
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
        // Attached as Unchanged with the lease as the original NextAttemptAtUtc: every bookkeeping save
        // below is a single-row UPDATE of what changed, conditional on that lease.
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Attach(message);

        string outcome;
        try
        {
            Result<Materialized> materialized = Materialize(message);
            outcome = materialized.IsFailure
                ? await DeadLetterUnreadableAsync(context, message, activity, materialized.Error)
                : await DispatchAsync(scope.ServiceProvider, context, message, materialized.Value, activity, stoppingToken);
        }
        catch (DbUpdateConcurrencyException exception) when (IsLostLease(exception))
        {
            // The save affected no row: NextAttemptAtUtc is no longer this processor's lease. The lease ran
            // out under a handler that ignored its token or a database slower than the budget, another
            // processor claimed the row (or an operator re-drove it) and owns its bookkeeping now. Whatever
            // this attempt wanted to write — a credit, Processed, a back-off, a dead letter — would overwrite
            // theirs, so it goes with the tracker; the credits checkpointed before the lease was lost were
            // saved under it and stand. A stale processor can therefore neither resurrect a Processed row
            // nor dead-letter the new owner's.
            context.ChangeTracker.Clear();
            outcome = LeaseLost(message, activity, exception, leaseUntil);
        }

        // Only booked outcomes get here: a stopping host's cancellation propagates out of DispatchAsync
        // (the caller hands the lease back), and an interrupted dispatch is not a duration to measure.
        metrics.DispatchDuration(message.Type, timeProvider.GetElapsedTime(started), outcome);
    }

    /// <summary>
    /// Runs the handlers under the budget and the originating tenant and books the outcome, which it
    /// returns. A stopping host's cancellation is not an outcome: it propagates instead.
    /// </summary>
    private async Task<string> DispatchAsync(
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
        catch (Exception exception) when (!IsLostLease(exception) && (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested))
        {
            // Keyed on the host's token, never the linked one: a downstream timeout (an
            // OperationCanceledException too) and the budget above are failures to retry; only a stopping
            // host is not. Neither is a lost lease — a checkpoint that found the row re-claimed is the
            // caller's to book, not an attempt to count. Whatever a handler left in this scope's tracker
            // was never committed (checkpoints commit per handler): forget it, re-attach the message as
            // modified and persist the failure alone.
            context.ChangeTracker.Clear();
            context.Update(message);
            return await BookFailureAsync(context, message, activity, exception, completed);
        }
    }

    /// <summary>
    /// Books one failed attempt — a back-off, or the dead letter once the attempts are exhausted — and
    /// returns the outcome tag. The counter and the log line follow the save: a retry signal for a row
    /// whose save then failed (the database gone, the lease lost) would announce a retry nobody scheduled.
    /// </summary>
    private async Task<string> BookFailureAsync(AppDbContext context, OutboxMessage message, Activity? activity, Exception exception, HashSet<string> completed)
    {
        // Attempts counts claims that ended in failure — exactly one increment per claim, however many
        // handlers ran. Update() rewrites every column, so the whole row is made consistent: pending, not
        // processed, carrying exactly the handlers credited so far (their checkpoints are durable).
        message.Attempts++;
        message.Status = OutboxMessageStatus.Pending;
        message.ProcessedAtUtc = null;
        message.CompletedHandlers = completed.Count > 0 ? JsonSerializer.Serialize(completed) : null;
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.SetTag(OutboxMetrics.ErrorTypeAttribute, exception.GetType().FullName);
        activity?.AddException(exception);

        if (message.Attempts >= options.MaxAttempts)
        {
            DeadLetter(message, activity, exception.Message, ExhaustedReason);
            await context.SaveChangesAsync(CancellationToken.None);
            AnnounceDeadLetter(message, ExhaustedReason);
            return DeadLetteredOutcome;
        }

        message.Error = exception.Message;
        message.NextAttemptAtUtc = timeProvider.GetUtcNow() + OutboxBackoff.DelayAfter(options, message.Attempts);
        activity?.SetTag(OutboxMetrics.OutcomeAttribute, RetryScheduledOutcome);
        await context.SaveChangesAsync(CancellationToken.None);

        // Warning, not Error: the retry is scheduled and the trellis.outbox.retries counter is the alarm
        // (log-level policy); the exception rides along so the cause is one search away.
        metrics.MessageRetried(message.Type, exception.GetType().Name);
        OutboxLog.RetryScheduled(logger, exception, message.Id, message.Type, message.Attempts, options.MaxAttempts, message.NextAttemptAtUtc);
        return RetryScheduledOutcome;
    }

    /// <summary>
    /// Expected, permanent failures are decided without exceptions: an unknown wire name, or a payload or
    /// CompletedHandlers column that does not parse. No retry can fix either, so the row is dead-lettered
    /// at once with Attempts untouched, and the rest of the batch goes on.
    /// </summary>
    private async Task<string> DeadLetterUnreadableAsync(AppDbContext context, OutboxMessage message, Activity? activity, Error error)
    {
        string reason = string.Equals(error.Code, OutboxErrors.UnknownEventTypeCode, StringComparison.Ordinal)
            ? UnknownTypeReason
            : PoisonReason;
        DeadLetter(message, activity, error.Description, reason);
        activity?.SetTag(OutboxMetrics.ErrorTypeAttribute, error.Code);
        await context.SaveChangesAsync(CancellationToken.None);
        AnnounceDeadLetter(message, reason);
        return DeadLetteredOutcome;
    }

    /// <summary>Writes the dead letter into the row and onto the span; the counter and the Critical line wait for the save (<see cref="AnnounceDeadLetter"/>).</summary>
    private static void DeadLetter(OutboxMessage message, Activity? activity, string reason, string reasonTag)
    {
        message.Status = OutboxMessageStatus.DeadLettered;
        message.ProcessedAtUtc = null;
        message.Error = reason;
        activity?.SetStatus(ActivityStatusCode.Error, reason);
        activity?.SetTag(OutboxMetrics.OutcomeAttribute, DeadLetteredOutcome);
        activity?.SetTag(OutboxMetrics.ReasonAttribute, reasonTag);
    }

    private void AnnounceDeadLetter(OutboxMessage message, string reasonTag)
    {
        metrics.MessageDeadLettered(message.Type, reasonTag);
        OutboxLog.DeadLettered(logger, message.Id, message.Type, message.Attempts, message.Error ?? reasonTag);
    }

    private string LeaseLost(OutboxMessage message, Activity? activity, Exception exception, DateTimeOffset leaseUntil)
    {
        activity?.SetStatus(ActivityStatusCode.Error, "The lease was lost: another processor claimed the row");
        activity?.SetTag(OutboxMetrics.ErrorTypeAttribute, exception.GetType().FullName);
        activity?.SetTag(OutboxMetrics.OutcomeAttribute, LeaseLostOutcome);
        OutboxLog.LeaseLost(logger, message.Id, message.Type, leaseUntil);
        return LeaseLostOutcome;
    }

    /// <summary>
    /// A save that found the message row's <c>NextAttemptAtUtc</c> changed under it — the lease is gone.
    /// Only the outbox row counts: a handler's own write losing a concurrency race at the checkpoint is
    /// that handler's ordinary, retryable failure.
    /// </summary>
    private static bool IsLostLease(Exception exception)
        => exception is DbUpdateConcurrencyException concurrency
           && concurrency.Entries.Any(static entry => entry.Entity is OutboxMessage);

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

            OutboxLog.LeasesReleased(logger, released);
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or RetryLimitExceededException)
        {
            OutboxLog.LeaseReleaseFailed(logger, exception, unfinished.Length, leaseUntil);
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
