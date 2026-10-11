using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace Trellis.SharedKernel.Domain;

/// <summary>
/// Resolves all IDomainEventHandler&lt;TEvent&gt; registrations for each event's runtime type and invokes
/// them sequentially, crediting each one under its stable <see cref="DomainEventHandlerNameAttribute"/>
/// and isolating each one's failure from its siblings.
/// </summary>
public sealed class DomainEventDispatcher(IServiceProvider serviceProvider) : IDomainEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, IHandlerInvoker> Invokers = new();
    private static readonly ConcurrentDictionary<Type, string> HandlerNames = new();

    public Task Dispatch(
        IDomainEvent domainEvent,
        ISet<string> completedHandlers,
        Func<string, Task>? checkpoint,
        Func<string, Exception, Task>? onHandlerFailed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(completedHandlers);

        return Invokers
            .GetOrAdd(
                domainEvent.GetType(),
                static eventType => (IHandlerInvoker)Activator.CreateInstance(typeof(TypedHandlerInvoker<>).MakeGenericType(eventType))!)
            .Invoke(domainEvent, serviceProvider, completedHandlers, checkpoint, onHandlerFailed, cancellationToken);
    }

    /// <summary>
    /// The stable completion key of a handler type — its <see cref="DomainEventHandlerNameAttribute"/>.
    /// A handler without one is a programming error, reported before any handler of the event runs.
    /// </summary>
    public static string HandlerNameOf(Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(handlerType);

        return HandlerNames.GetOrAdd(
            handlerType,
            static type => type.GetCustomAttribute<DomainEventHandlerNameAttribute>()?.Name is { Length: > 0 } name
                ? name
                : throw new InvalidOperationException(
                    $"Domain-event handler '{type.FullName}' has no [DomainEventHandlerName(\"...\")]. Every handler needs a stable "
                    + "completion key so a retry never re-runs it after a rename, e.g. [DomainEventHandlerName(\"invoices.paid.send-receipt\")]."));
    }

    private interface IHandlerInvoker
    {
        Task Invoke(
            IDomainEvent domainEvent,
            IServiceProvider serviceProvider,
            ISet<string> completedHandlers,
            Func<string, Task>? checkpoint,
            Func<string, Exception, Task>? onHandlerFailed,
            CancellationToken cancellationToken);
    }

    private sealed class TypedHandlerInvoker<TEvent> : IHandlerInvoker
        where TEvent : IDomainEvent
    {
        public async Task Invoke(
            IDomainEvent domainEvent,
            IServiceProvider serviceProvider,
            ISet<string> completedHandlers,
            Func<string, Task>? checkpoint,
            Func<string, Exception, Task>? onHandlerFailed,
            CancellationToken cancellationToken)
        {
            (IDomainEventHandler<TEvent> Handler, string Name)[] handlers = ResolveHandlers(serviceProvider);
            List<(string Name, Exception Exception)>? failures = null;

            foreach ((IDomainEventHandler<TEvent> handler, string name) in handlers)
            {
                if (completedHandlers.Contains(name))
                {
                    continue;
                }

                try
                {
                    await handler.Handle((TEvent)domainEvent, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // This handler's failure alone: it stays uncredited, so it alone is retried, and its
                    // siblings still get their turn — an outage behind one of them must not delay the others.
                    // A cancellation of the token handed in (the host stopping, the caller's budget spent) is
                    // nobody's failure in particular: it propagates and ends the dispatch here, because every
                    // handler after it would only fail the same way.
                    (failures ??= []).Add((name, exception));
                    if (onHandlerFailed is not null)
                    {
                        await onHandlerFailed(name, exception);
                    }

                    continue;
                }

                // Never isolated: a checkpoint that cannot commit leaves this handler uncredited AND ends the
                // dispatch — nothing after it could be committed either.
                if (checkpoint is not null)
                {
                    await checkpoint(name);
                }

                completedHandlers.Add(name);
            }

            ThrowIfAnyFailed(failures, handlers.Length);
        }

        private static (IDomainEventHandler<TEvent> Handler, string Name)[] ResolveHandlers(IServiceProvider serviceProvider)
        {
            // Every name is resolved before anything runs: a missing or shared key fails the whole
            // dispatch up front instead of after some handlers already had their side effects (a shared
            // key would make the second handler look credited and skip it forever).
            (IDomainEventHandler<TEvent> Handler, string Name)[] handlers = [.. serviceProvider
                .GetServices<IDomainEventHandler<TEvent>>()
                .Select(static handler => (handler, HandlerNameOf(handler.GetType())))];

            if (handlers.Select(static h => h.Name).Distinct(StringComparer.Ordinal).Count() != handlers.Length)
            {
                throw new InvalidOperationException(
                    $"Two handlers of '{typeof(TEvent).FullName}' share a [DomainEventHandlerName]: "
                    + string.Join(", ", handlers.Select(static h => $"{h.Handler.GetType().FullName} = \"{h.Name}\"")));
            }

            return handlers;
        }

        private static void ThrowIfAnyFailed(List<(string Name, Exception Exception)>? failures, int handlerCount)
        {
            if (failures is null)
            {
                return;
            }

            if (failures.Count == 1)
            {
                // As it was thrown, stack intact: the retry's error.type and log line keep their meaning.
                ExceptionDispatchInfo.Throw(failures[0].Exception);
            }

            throw new AggregateException(
                $"{failures.Count} of the {handlerCount} handlers of '{typeof(TEvent).FullName}' failed: "
                + string.Join(", ", failures.Select(static f => f.Name)),
                failures.Select(static f => f.Exception));
        }
    }
}
