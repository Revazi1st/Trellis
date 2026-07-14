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

        Result<Money> amount = Money.Create(command.Amount, invoice.Currency);
        if (amount.IsFailure)
        {
            return Result.Failure(amount.Error);
        }

        return invoice.AddLine(command.Description, amount.Value);
    }
}
