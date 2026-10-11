using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Invoices.VoidInvoice;

internal sealed class VoidInvoiceCommandHandler(IInvoiceRepository invoices)
    : ICommandHandler<VoidInvoiceCommand>
{
    public async Task<Result> Handle(VoidInvoiceCommand command, CancellationToken cancellationToken)
    {
        Invoice? loaded = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);

        return loaded.ToWrite(command.InvoiceId, command.ExpectedVersion)
            .Bind(static invoice => invoice.Void());
    }
}
