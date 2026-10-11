using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Invoices.AddInvoiceLine;

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

        // Money spans two inputs and AddLine has several rules, so only the codes that belong to one
        // request member are attributed to it; the rest stay whole-request errors.
        return Money.Create(command.Amount, invoice.Currency)
            .AtField(nameof(command.Amount), MoneyErrors.NegativeAmount, MoneyErrors.TooManyDecimalPlaces)
            .Bind(amount => invoice.AddLine(command.Description, amount)
                .AtField(nameof(command.Description), InvoiceErrors.EmptyLineDescription));
    }
}
