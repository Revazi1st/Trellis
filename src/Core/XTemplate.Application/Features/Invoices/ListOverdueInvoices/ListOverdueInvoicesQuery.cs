using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Invoices.ListOverdueInvoices;

public sealed record ListOverdueInvoicesQuery : IQuery<IReadOnlyList<InvoiceResponse>>;
