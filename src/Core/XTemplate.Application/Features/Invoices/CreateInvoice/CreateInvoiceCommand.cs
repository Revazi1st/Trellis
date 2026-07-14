using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Invoices.CreateInvoice;

public sealed record CreateInvoiceCommand(string CustomerName, string CustomerEmail, string Currency)
    : ICommand<InvoiceId>;
