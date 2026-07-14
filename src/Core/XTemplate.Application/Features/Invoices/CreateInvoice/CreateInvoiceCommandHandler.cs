using XTemplate.Domain.Common;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Features.Invoices.CreateInvoice;

internal sealed class CreateInvoiceCommandHandler(IInvoiceRepository invoices)
    : ICommandHandler<CreateInvoiceCommand, InvoiceId>
{
    public Task<Result<InvoiceId>> Handle(CreateInvoiceCommand command, CancellationToken cancellationToken)
    {
        Result<EmailAddress> email = EmailAddress.Create(command.CustomerEmail);
        if (email.IsFailure)
        {
            return Task.FromResult<Result<InvoiceId>>(email.Error);
        }

        Result<Invoice> invoice = Invoice.Create(command.CustomerName, email.Value, command.Currency);
        if (invoice.IsFailure)
        {
            return Task.FromResult<Result<InvoiceId>>(invoice.Error);
        }

        invoices.Add(invoice.Value);

        return Task.FromResult<Result<InvoiceId>>(invoice.Value.Id);
    }
}
