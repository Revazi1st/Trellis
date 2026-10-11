using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Trellis.SharedKernel.Specifications;

namespace Trellis.Application.Features.Invoices.ListInvoices;

internal sealed class ListInvoicesQueryHandler(IInvoiceRepository invoices)
    : IQueryHandler<ListInvoicesQuery, PagedList<InvoiceResponse>>
{
    public async Task<Result<PagedList<InvoiceResponse>>> Handle(
        ListInvoicesQuery query,
        CancellationToken cancellationToken)
    {
        PagedList<Invoice> page = await invoices.ListPagedAsync(
            InvoiceSpecs.All(query.Status),
            query.Page,
            query.PageSize,
            cancellationToken);

        return page.Map(static invoice => invoice.ToResponse());
    }
}
