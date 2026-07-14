using XTemplate.SharedKernel.Domain;

namespace XTemplate.Domain.Invoices.Events;

public sealed record InvoiceIssuedDomainEvent(InvoiceId InvoiceId) : IDomainEvent;
