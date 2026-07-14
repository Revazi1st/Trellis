using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Features.Invoices.ListOverdueInvoices;

internal sealed class ListOverdueInvoicesQueryHandler(IInvoiceRepository invoices, TimeProvider timeProvider)
    : IQueryHandler<ListOverdueInvoicesQuery, IReadOnlyList<InvoiceResponse>>
{
    public async Task<Result<IReadOnlyList<InvoiceResponse>>> Handle(
        ListOverdueInvoicesQuery query,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Invoice> overdue = await invoices.ListAsync(
            InvoiceSpecs.Overdue(timeProvider.GetUtcNow()),
            cancellationToken);

        return Result.Success<IReadOnlyList<InvoiceResponse>>([.. overdue.Select(static i => i.ToResponse())]);
    }
}
