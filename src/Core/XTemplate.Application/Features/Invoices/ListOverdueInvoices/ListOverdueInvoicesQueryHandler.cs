using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;
using XTemplate.SharedKernel.Specifications;

namespace XTemplate.Application.Features.Invoices.ListOverdueInvoices;

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
