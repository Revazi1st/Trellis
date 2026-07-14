using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Persistence.Interceptors;

/// <summary>
/// Dispatches aggregate domain events immediately before SaveChanges so handler side effects join the same
/// transaction (ADR-0006; a transactional outbox is the documented production evolution for cross-service effects).
/// Registered first: handlers may add or modify entities that the other interceptors must still see.
/// </summary>
public sealed class DomainEventDispatchInterceptor(IDomainEventDispatcher dispatcher) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await DispatchPendingEvents(eventData.Context, cancellationToken);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        => throw new NotSupportedException(
            "Domain events require the async pipeline — call SaveChangesAsync (the UnitOfWork decorator already does).");

    private async Task DispatchPendingEvents(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return;
        }

        // Handlers may raise further events or touch more aggregates; drain until quiescent.
        while (true)
        {
            List<IHasDomainEvents> aggregates = [.. context.ChangeTracker
                .Entries<IHasDomainEvents>()
                .Where(static entry => entry.Entity.DomainEvents.Count > 0)
                .Select(static entry => entry.Entity)];

            if (aggregates.Count == 0)
            {
                return;
            }

            List<IDomainEvent> domainEvents = [.. aggregates.SelectMany(static aggregate => aggregate.DomainEvents)];

            foreach (IHasDomainEvents aggregate in aggregates)
            {
                aggregate.ClearDomainEvents();
            }

            await dispatcher.Dispatch(domainEvents, cancellationToken);
        }
    }
}
