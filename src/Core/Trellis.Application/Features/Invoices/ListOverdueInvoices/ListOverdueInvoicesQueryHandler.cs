using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Trellis.SharedKernel.Specifications;

namespace Trellis.Application.Features.Invoices.ListOverdueInvoices;

internal sealed class ListOverdueInvoicesQueryHandler(IInvoiceRepository invoices, TimeProvider timeProvider)
    : IQueryHandler<ListOverdueInvoicesQuery, PagedList<InvoiceResponse>>
{
    public async Task<Result<PagedList<InvoiceResponse>>> Handle(
        ListOverdueInvoicesQuery query,
        CancellationToken cancellationToken)
    {
        PagedList<Invoice> page = await invoices.ListPagedAsync(
            InvoiceSpecs.Overdue(timeProvider.GetUtcNow()),
            query.Page,
            query.PageSize,
            cancellationToken);

        return page.Map(static invoice => invoice.ToResponse());
    }
}
