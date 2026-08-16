using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace XTemplate.SharedKernel.Domain;

/// <summary>Resolves all IDomainEventHandler&lt;TEvent&gt; registrations for each event's runtime type and invokes them sequentially.</summary>
public sealed class DomainEventDispatcher(IServiceProvider serviceProvider) : IDomainEventDispatcher
{
    private static readonly ConcurrentDictionary<Type, IHandlerInvoker> Invokers = new();

    public async Task Dispatch(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        foreach (IDomainEvent domainEvent in domainEvents)
        {
            await InvokerFor(domainEvent).Invoke(domainEvent, serviceProvider, completedHandlers: null, cancellationToken);
        }
    }

    public Task Dispatch(IDomainEvent domainEvent, ISet<string> completedHandlers, CancellationToken cancellationToken)
        => InvokerFor(domainEvent).Invoke(domainEvent, serviceProvider, completedHandlers, cancellationToken);

    private static IHandlerInvoker InvokerFor(IDomainEvent domainEvent) => Invokers.GetOrAdd(
        domainEvent.GetType(),
        static eventType => (IHandlerInvoker)Activator.CreateInstance(
            typeof(TypedHandlerInvoker<>).MakeGenericType(eventType))!);

    private interface IHandlerInvoker
    {
        Task Invoke(IDomainEvent domainEvent, IServiceProvider serviceProvider, ISet<string>? completedHandlers, CancellationToken cancellationToken);
    }

    private sealed class TypedHandlerInvoker<TEvent> : IHandlerInvoker
        where TEvent : IDomainEvent
    {
        public async Task Invoke(IDomainEvent domainEvent, IServiceProvider serviceProvider, ISet<string>? completedHandlers, CancellationToken cancellationToken)
        {
            foreach (IDomainEventHandler<TEvent> handler in serviceProvider.GetServices<IDomainEventHandler<TEvent>>())
            {
                string handlerName = handler.GetType().FullName!;
                if (completedHandlers?.Contains(handlerName) == true)
                {
                    continue;
                }

                await handler.Handle((TEvent)domainEvent, cancellationToken);
                completedHandlers?.Add(handlerName);
            }
        }
    }
}
