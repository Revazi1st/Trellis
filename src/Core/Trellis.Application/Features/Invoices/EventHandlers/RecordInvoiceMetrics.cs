using Trellis.Application.Observability;
using Trellis.Domain.Invoices.Events;
using Trellis.SharedKernel.Domain;

namespace Trellis.Application.Features.Invoices.EventHandlers;

/// <summary>Counts issued invoices once the issuing transaction has committed (dispatched via the outbox).</summary>
[DomainEventHandlerName("invoices.issued.record-metrics")]
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
[DomainEventHandlerName("invoices.paid.record-metrics")]
internal sealed class RecordMetricsOnInvoicePaid(InvoiceMetrics metrics)
    : IDomainEventHandler<InvoicePaidDomainEvent>
{
    public Task Handle(InvoicePaidDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        metrics.InvoicePaid();
        return Task.CompletedTask;
    }
}
