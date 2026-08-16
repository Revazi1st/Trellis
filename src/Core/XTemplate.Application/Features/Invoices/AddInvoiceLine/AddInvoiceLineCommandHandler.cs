using XTemplate.Domain.Common;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Features.Invoices.AddInvoiceLine;

internal sealed class AddInvoiceLineCommandHandler(IInvoiceRepository invoices)
    : ICommandHandler<AddInvoiceLineCommand>
{
    public async Task<Result> Handle(AddInvoiceLineCommand command, CancellationToken cancellationToken)
    {
        Invoice? invoice = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);
        if (invoice is null)
        {
            return Result.Failure(InvoiceErrors.NotFound(command.InvoiceId));
        }

        return Money.Create(command.Amount, invoice.Currency)
            .Bind(amount => invoice.AddLine(command.Description, amount));
    }
}
