using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Invoices.GetInvoice;

public sealed record GetInvoiceQuery(InvoiceId InvoiceId) : IQuery<InvoiceResponse>;
