using Trellis.Application.Abstractions;
using Trellis.Domain.Invoices.Events;
using Trellis.SharedKernel.Domain;

namespace Trellis.Application.Features.Invoices.EventHandlers;

/// <summary>Reacts to InvoicePaidDomainEvent (dispatched during SaveChanges) by emailing a receipt — the reference ports-and-adapters loop.</summary>
[DomainEventHandlerName("invoices.paid.send-receipt")]
internal sealed class SendReceiptOnInvoicePaid(IEmailSender emailSender)
    : IDomainEventHandler<InvoicePaidDomainEvent>
{
    public Task Handle(InvoicePaidDomainEvent domainEvent, CancellationToken cancellationToken)
        => emailSender.SendAsync(
            domainEvent.CustomerEmail,
            "Thanks for your payment",
            $"We received the payment for invoice {domainEvent.InvoiceId}.",
            cancellationToken);
}
