using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Invoices.GetInvoice;

internal sealed class GetInvoiceQueryHandler(IInvoiceRepository invoices)
    : IQueryHandler<GetInvoiceQuery, InvoiceResponse>
{
    public async Task<Result<InvoiceResponse>> Handle(GetInvoiceQuery query, CancellationToken cancellationToken)
    {
        Invoice? invoice = await invoices.GetByIdAsync(query.InvoiceId, cancellationToken);

        return invoice is null
            ? Result.Failure<InvoiceResponse>(InvoiceErrors.NotFound(query.InvoiceId))
            : invoice.ToResponse();
    }
}
