using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Invoices.CreateInvoice;

internal sealed class CreateInvoiceCommandHandler(IInvoiceRepository invoices)
    : ICommandHandler<CreateInvoiceCommand, InvoiceId>
{
    public Task<Result<InvoiceId>> Handle(CreateInvoiceCommand command, CancellationToken cancellationToken)
        // Combine validates every value object before failing, so the caller sees all the problems
        // at once instead of fixing them one 400 at a time; on success it hands both values on.
        // AtField names the request member each failure belongs to (errors[].field on the wire).
        => Task.FromResult(
            Result.Combine(
                    EmailAddress.Create(command.CustomerEmail).AtField(nameof(command.CustomerEmail)),
                    Currency.Create(command.Currency).AtField(nameof(command.Currency)))
                .Bind((email, currency) => Invoice.Create(command.CustomerName, email, currency)
                    .AtField(nameof(command.CustomerName)))
                .Tap(invoices.Add)
                .Map(static invoice => invoice.Id));
}
