using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Trellis.SharedKernel.Domain;

/// <summary>
/// Resolves all IDomainEventHandler&lt;TEvent&gt; registrations for each event's runtime type and invokes
/// them sequentially, crediting each one under its stable <see cref="DomainEventHandlerNameAttribute"/>.
/// </summary>
public sealed class DomainEventDispatcher(IServiceProvider serviceProvider) : IDomainEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, IHandlerInvoker> Invokers = new();
    private static readonly ConcurrentDictionary<Type, string> HandlerNames = new();

    public Task Dispatch(
        IDomainEvent domainEvent,
        ISet<string> completedHandlers,
        Func<string, Task>? checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(completedHandlers);

        return Invokers
            .GetOrAdd(
                domainEvent.GetType(),
                static eventType => (IHandlerInvoker)Activator.CreateInstance(typeof(TypedHandlerInvoker<>).MakeGenericType(eventType))!)
            .Invoke(domainEvent, serviceProvider, completedHandlers, checkpoint, cancellationToken);
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
            CancellationToken cancellationToken)
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

            foreach ((IDomainEventHandler<TEvent> handler, string name) in handlers)
            {
                if (completedHandlers.Contains(name))
                {
                    continue;
                }

                await handler.Handle((TEvent)domainEvent, cancellationToken);

                if (checkpoint is not null)
                {
                    await checkpoint(name);
                }

                completedHandlers.Add(name);
            }
        }
    }
}
