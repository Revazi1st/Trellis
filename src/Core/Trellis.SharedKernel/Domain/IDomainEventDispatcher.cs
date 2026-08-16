namespace Trellis.SharedKernel.Domain;

public interface IDomainEventDispatcher
{
    Task Dispatch(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);

    /// <summary>
    /// Dispatches one event with per-handler bookkeeping: handlers whose full names are already in
    /// <paramref name="completedHandlers"/> are skipped, and each handler that succeeds is added to
    /// the set before the next one runs. A caller that persists the set across retries (the outbox)
    /// therefore never re-runs a handler that already succeeded — no double-sent email or
    /// double-counted metric when a later handler fails.
    /// </summary>
    Task Dispatch(IDomainEvent domainEvent, ISet<string> completedHandlers, CancellationToken cancellationToken);
}
