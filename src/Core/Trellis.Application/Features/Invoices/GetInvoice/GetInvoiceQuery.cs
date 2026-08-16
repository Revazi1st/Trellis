using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Invoices.GetInvoice;

public sealed record GetInvoiceQuery(InvoiceId InvoiceId) : IQuery<InvoiceResponse>;
