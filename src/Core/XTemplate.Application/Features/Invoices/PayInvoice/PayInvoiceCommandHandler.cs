using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Features.Invoices.PayInvoice;

internal sealed class PayInvoiceCommandHandler(IInvoiceRepository invoices, TimeProvider timeProvider)
    : ICommandHandler<PayInvoiceCommand>
{
    public async Task<Result> Handle(PayInvoiceCommand command, CancellationToken cancellationToken)
    {
        Invoice? invoice = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);

        return invoice is null
            ? Result.Failure(InvoiceErrors.NotFound(command.InvoiceId))
            : invoice.MarkPaid(timeProvider.GetUtcNow());
    }
}
