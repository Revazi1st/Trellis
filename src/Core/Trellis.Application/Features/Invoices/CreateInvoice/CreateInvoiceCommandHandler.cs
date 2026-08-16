using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Invoices.CreateInvoice;

internal sealed class CreateInvoiceCommandHandler(IInvoiceRepository invoices)
    : ICommandHandler<CreateInvoiceCommand, InvoiceId>
{
    public Task<Result<InvoiceId>> Handle(CreateInvoiceCommand command, CancellationToken cancellationToken)
    {
        Result<EmailAddress> email = EmailAddress.Create(command.CustomerEmail);
        Result<Currency> currency = Currency.Create(command.Currency);

        // Combine validates every value object before failing, so the caller sees all the problems
        // at once instead of fixing them one 400 at a time.
        Result combined = Result.Combine(email, currency);
        if (combined.IsFailure)
        {
            return Task.FromResult(Result.Failure<InvoiceId>(combined.Error));
        }

        return Task.FromResult(
            Invoice.Create(command.CustomerName, email.Value, currency.Value)
                .Tap(invoices.Add)
                .Map(static invoice => invoice.Id));
    }
}
