using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Invoices.IssueInvoice;

internal sealed class IssueInvoiceCommandHandler(IInvoiceRepository invoices, TimeProvider timeProvider)
    : ICommandHandler<IssueInvoiceCommand>
{
    public async Task<Result> Handle(IssueInvoiceCommand command, CancellationToken cancellationToken)
    {
        Invoice? loaded = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);

        return loaded.ToWrite(command.InvoiceId, command.ExpectedVersion)
            .Bind(invoice => invoice.Issue(command.DueDate, timeProvider.GetUtcNow())
                .AtField(nameof(command.DueDate), InvoiceErrors.DueDateInPast));
    }
}
