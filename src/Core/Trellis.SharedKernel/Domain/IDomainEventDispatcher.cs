namespace Trellis.SharedKernel.Domain;

public interface IDomainEventDispatcher
{
    /// <summary>
    /// Dispatches one event with per-handler bookkeeping. Every handler is identified by its stable
    /// <see cref="DomainEventHandlerNameAttribute"/> name, never its CLR name: handlers already in
    /// <paramref name="completedHandlers"/> are skipped, and each handler that succeeds is credited
    /// (added to the set) before the next one runs. A caller that persists the set across retries (the
    /// outbox) therefore never re-runs a handler that already succeeded — no double-sent email or
    /// double-counted metric when a later handler fails.
    /// </summary>
    /// <param name="domainEvent">The event to dispatch.</param>
    /// <param name="completedHandlers">The names of the handlers already credited for this event; updated in place.</param>
    /// <param name="checkpoint">
    /// Invoked with the handler's name after it succeeded and <b>before</b> it is credited. If it throws,
    /// the handler stays uncredited and the exception propagates, so "credited" always means "durable":
    /// the outbox uses it to commit the handler's unit-of-work writes together with its credit. It gets
    /// no cancellation token on purpose: it is bookkeeping for work that already happened, and a stopping
    /// host must not be able to abandon it halfway.
    /// </param>
    /// <param name="cancellationToken">Passed to every handler.</param>
    Task Dispatch(
        IDomainEvent domainEvent,
        ISet<string> completedHandlers,
        Func<string, Task>? checkpoint,
        CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="Dispatch(IDomainEvent, ISet{string}, Func{string, Task}, CancellationToken)"/> without
    /// a checkpoint: credits live only in <paramref name="completedHandlers"/>, for a caller that persists
    /// the set itself once dispatch ends.
    /// </summary>
    Task Dispatch(IDomainEvent domainEvent, ISet<string> completedHandlers, CancellationToken cancellationToken)
        => Dispatch(domainEvent, completedHandlers, checkpoint: null, cancellationToken);
}
