using XTemplate.Application.Observability;
using XTemplate.Domain.Invoices.Events;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Application.Features.Invoices.EventHandlers;

/// <summary>Counts issued invoices once the issuing transaction has committed (dispatched via the outbox).</summary>
internal sealed class RecordMetricsOnInvoiceIssued(InvoiceMetrics metrics)
    : IDomainEventHandler<InvoiceIssuedDomainEvent>
{
    public Task Handle(InvoiceIssuedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        metrics.InvoiceIssued(domainEvent.Currency);
        return Task.CompletedTask;
    }
}

/// <summary>Counts paid invoices once the payment transaction has committed (dispatched via the outbox).</summary>
internal sealed class RecordMetricsOnInvoicePaid(InvoiceMetrics metrics)
    : IDomainEventHandler<InvoicePaidDomainEvent>
{
    public Task Handle(InvoicePaidDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        metrics.InvoicePaid();
        return Task.CompletedTask;
    }
}
