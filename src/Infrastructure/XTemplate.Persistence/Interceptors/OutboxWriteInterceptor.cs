using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
#if (UseMultitenancy)
using XTemplate.Application.Abstractions;
#endif
using XTemplate.Persistence.Outbox;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Persistence.Interceptors;

/// <summary>
/// Transactional outbox (ADR-0018): instead of running domain-event handlers inside SaveChanges,
/// this serializes each pending event into an OutboxMessage row in the SAME transaction. A
/// background OutboxProcessor dispatches them after commit — so side effects never run on a
/// rolled-back or retried transaction. Registered first in the interceptor chain.
/// </summary>
public sealed class OutboxWriteInterceptor(
    TimeProvider timeProvider
#if (UseMultitenancy)
    , ITenantContext tenantContext
#endif
    ) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        WriteOutbox(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        WriteOutbox(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void WriteOutbox(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        List<IHasDomainEvents> aggregates = [.. context.ChangeTracker
            .Entries<IHasDomainEvents>()
            .Where(static entry => entry.Entity.DomainEvents.Count > 0)
            .Select(static entry => entry.Entity)];

        if (aggregates.Count == 0)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        List<OutboxMessage> messages = [.. aggregates
            .SelectMany(static aggregate => aggregate.DomainEvents)
            .Select(domainEvent => new OutboxMessage
            {
                Id = Guid.CreateVersion7(),
                Type = DomainEventSerialization.TypeNameOf(domainEvent),
                Content = DomainEventSerialization.Serialize(domainEvent),
                OccurredAtUtc = now,
                NextAttemptAtUtc = now,
                // Carried across the async boundary: dispatch spans join the originating trace, and
                // handlers run under the originating tenant.
                TraceParent = Activity.Current?.Id,
#if (UseMultitenancy)
                TenantId = tenantContext.TenantId,
#endif
            })];

        foreach (IHasDomainEvents aggregate in aggregates)
        {
            aggregate.ClearDomainEvents();
        }

        context.Set<OutboxMessage>().AddRange(messages);
    }
}
