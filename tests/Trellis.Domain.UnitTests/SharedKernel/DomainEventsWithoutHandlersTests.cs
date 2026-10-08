using Shouldly;
using Trellis.Domain.Invoices;
using Trellis.Domain.Invoices.Events;
using Trellis.SharedKernel.Domain;
using Xunit;

namespace Trellis.Domain.UnitTests.SharedKernel;

/// <summary>
/// An event may have no subscriber at all: <see cref="InvoiceVoidedDomainEvent"/> ships without one. The
/// outbox marks a message processed once Dispatch returns, so Dispatch must return normally (no throw,
/// no phantom entry in the completed-handler set) when the container holds no handler for the event.
/// </summary>
public sealed class DomainEventsWithoutHandlersTests
{
    [Fact]
    public async Task Dispatching_an_event_nobody_handles_completes_and_records_nothing()
    {
        var dispatcher = new DomainEventDispatcher(new ContainerWithoutHandlers());
        var completedHandlers = new HashSet<string>(StringComparer.Ordinal);
        IDomainEvent voided = new InvoiceVoidedDomainEvent(InvoiceId.New());

        await dispatcher.Dispatch(voided, completedHandlers, TestContext.Current.CancellationToken);
        await dispatcher.Dispatch([voided], TestContext.Current.CancellationToken);

        completedHandlers.ShouldBeEmpty();
    }

    /// <summary>
    /// Answers as Microsoft.Extensions.DependencyInjection does for a service nobody registered: an empty
    /// sequence for <c>IEnumerable&lt;T&gt;</c> (what <c>GetServices</c> asks for), null for anything else.
    /// </summary>
    private sealed class ContainerWithoutHandlers : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? Array.CreateInstance(serviceType.GetGenericArguments()[0], 0)
                : null;
    }
}
