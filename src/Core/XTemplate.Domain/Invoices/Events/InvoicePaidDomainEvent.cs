using XTemplate.Domain.Common;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Domain.Invoices.Events;

public sealed record InvoicePaidDomainEvent(InvoiceId InvoiceId, EmailAddress CustomerEmail) : IDomainEvent;
