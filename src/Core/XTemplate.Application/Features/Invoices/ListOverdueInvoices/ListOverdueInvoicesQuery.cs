using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Specifications;

namespace XTemplate.Application.Features.Invoices.ListOverdueInvoices;

public sealed record ListOverdueInvoicesQuery(int Page = 1, int PageSize = 20)
    : IQuery<PagedList<InvoiceResponse>>;
