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
        Invoice? loaded = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);

        // The currency is the invoice's, already a valid Currency, so every Money failure is about the
        // amount. AddLine has several rules: only the description's are attributed to that member; the
        // rest stay whole-request errors.
        return loaded.ToWrite(command.InvoiceId, command.ExpectedVersion)
            .Bind(invoice => Money.Create(command.Amount, invoice.Currency)
                .AtField(nameof(command.Amount))
                .Bind(amount => invoice.AddLine(command.Description, amount)
                    .AtField(nameof(command.Description), InvoiceErrors.EmptyLineDescription, InvoiceErrors.LineDescriptionTooLong)));
    }
}
