namespace Trellis.SharedKernel.Domain;

public interface IDomainEventDispatcher
{
    /// <summary>
    /// Dispatches one event with per-handler bookkeeping and per-handler isolation. Every handler is
    /// identified by its stable <see cref="DomainEventHandlerNameAttribute"/> name, never its CLR name:
    /// handlers already in <paramref name="completedHandlers"/> are skipped, and each handler that
    /// succeeds is credited (added to the set) before the next one runs. A caller that persists the set
    /// across retries (the outbox) therefore never re-runs a handler that already succeeded — no
    /// double-sent email or double-counted metric when another handler fails.
    /// <para>
    /// Every uncredited handler gets its turn on every attempt: a handler's own exception is caught,
    /// reported through <paramref name="onHandlerFailed"/> and remembered, and its siblings still run, so
    /// an outage behind one handler never delays the others. Once the last handler has run, a single
    /// failure is rethrown as it was (its type keeps its meaning for the retry's telemetry) and several
    /// are thrown as one <see cref="AggregateException"/> naming the handlers. A cancellation of
    /// <paramref name="cancellationToken"/> — the host stopping, or the caller's budget spent — is not a
    /// handler's failure: it ends the dispatch at once, the handlers after it untouched.
    /// </para>
    /// </summary>
    /// <param name="domainEvent">The event to dispatch.</param>
    /// <param name="completedHandlers">The names of the handlers already credited for this event; updated in place.</param>
    /// <param name="checkpoint">
    /// Invoked with the handler's name after it succeeded and <b>before</b> it is credited. If it throws,
    /// the handler stays uncredited and the exception propagates at once — a checkpoint failure is never
    /// isolated, because nothing after it could be committed either — so "credited" always means
    /// "durable": the outbox uses it to commit the handler's unit-of-work writes together with its credit.
    /// It gets no cancellation token on purpose: it is bookkeeping for work that already happened, and a
    /// stopping host must not be able to abandon it halfway.
    /// </param>
    /// <param name="onHandlerFailed">
    /// Invoked with the handler's name and its exception when a handler threw, before the next handler
    /// runs: the outbox drops whatever the failed handler left uncommitted, so the next checkpoint commits
    /// its own handler's writes alone. If it throws, the exception propagates and ends the dispatch.
    /// </param>
    /// <param name="cancellationToken">Passed to every handler.</param>
    Task Dispatch(
        IDomainEvent domainEvent,
        ISet<string> completedHandlers,
        Func<string, Task>? checkpoint,
        Func<string, Exception, Task>? onHandlerFailed,
        CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="Dispatch(IDomainEvent, ISet{string}, Func{string, Task}, Func{string, Exception, Task}, CancellationToken)"/>
    /// without a failure hook, for a caller whose handlers leave nothing behind that a later checkpoint could commit.
    /// </summary>
    Task Dispatch(IDomainEvent domainEvent, ISet<string> completedHandlers, Func<string, Task>? checkpoint, CancellationToken cancellationToken)
        => Dispatch(domainEvent, completedHandlers, checkpoint, onHandlerFailed: null, cancellationToken);

    /// <summary>
    /// <see cref="Dispatch(IDomainEvent, ISet{string}, Func{string, Task}, Func{string, Exception, Task}, CancellationToken)"/>
    /// without a checkpoint: credits live only in <paramref name="completedHandlers"/>, for a caller that
    /// persists the set itself once dispatch ends.
    /// </summary>
    Task Dispatch(IDomainEvent domainEvent, ISet<string> completedHandlers, CancellationToken cancellationToken)
        => Dispatch(domainEvent, completedHandlers, checkpoint: null, onHandlerFailed: null, cancellationToken);
}
