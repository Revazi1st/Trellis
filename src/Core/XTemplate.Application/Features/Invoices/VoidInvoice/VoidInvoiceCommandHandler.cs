using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Features.Invoices.VoidInvoice;

internal sealed class VoidInvoiceCommandHandler(IInvoiceRepository invoices)
    : ICommandHandler<VoidInvoiceCommand>
{
    public async Task<Result> Handle(VoidInvoiceCommand command, CancellationToken cancellationToken)
    {
        Invoice? invoice = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);

        return invoice is null
            ? Result.Failure(InvoiceErrors.NotFound(command.InvoiceId))
            : invoice.Void();
    }
}
