using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Invoices.PayInvoice;

internal sealed class PayInvoiceCommandHandler(IInvoiceRepository invoices, TimeProvider timeProvider)
    : ICommandHandler<PayInvoiceCommand>
{
    public async Task<Result> Handle(PayInvoiceCommand command, CancellationToken cancellationToken)
    {
        Invoice? loaded = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);

        return loaded.ToWrite(command.InvoiceId, command.ExpectedVersion)
            .Bind(invoice => invoice.MarkPaid(timeProvider.GetUtcNow()));
    }
}
