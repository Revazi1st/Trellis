using XTemplate.Application.Abstractions;
using XTemplate.Domain.Invoices.Events;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Application.Features.Invoices.EventHandlers;

/// <summary>Reacts to InvoicePaidDomainEvent (dispatched during SaveChanges) by emailing a receipt — the reference ports-and-adapters loop.</summary>
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
