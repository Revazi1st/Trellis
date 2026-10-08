using Trellis.SharedKernel.Domain;

namespace Trellis.Domain.Invoices.Events;

/// <summary>Raised when a draft or issued invoice is voided. No handler ships with the template; subscribe when a reaction is needed.</summary>
[DomainEventName("invoices.voided.v1")]
public sealed record InvoiceVoidedDomainEvent(InvoiceId InvoiceId) : IDomainEvent;
