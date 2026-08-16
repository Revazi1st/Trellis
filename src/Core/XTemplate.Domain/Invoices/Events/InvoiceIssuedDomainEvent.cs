using XTemplate.SharedKernel.Domain;

namespace XTemplate.Domain.Invoices.Events;

/// <summary>Domain events crossing the outbox use primitive/simple types so they JSON round-trip.</summary>
[DomainEventName("invoices.issued.v1")]
public sealed record InvoiceIssuedDomainEvent(InvoiceId InvoiceId, string Currency) : IDomainEvent;
