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
            IHandlerInvoker invoker = Invokers.GetOrAdd(
                domainEvent.GetType(),
                static eventType => (IHandlerInvoker)Activator.CreateInstance(
                    typeof(TypedHandlerInvoker<>).MakeGenericType(eventType))!);

            await invoker.Invoke(domainEvent, serviceProvider, cancellationToken);
        }
    }

    private interface IHandlerInvoker
    {
        Task Invoke(IDomainEvent domainEvent, IServiceProvider serviceProvider, CancellationToken cancellationToken);
    }

    private sealed class TypedHandlerInvoker<TEvent> : IHandlerInvoker
        where TEvent : IDomainEvent
    {
        public async Task Invoke(IDomainEvent domainEvent, IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            foreach (IDomainEventHandler<TEvent> handler in serviceProvider.GetServices<IDomainEventHandler<TEvent>>())
            {
                await handler.Handle((TEvent)domainEvent, cancellationToken);
            }
        }
    }
}
