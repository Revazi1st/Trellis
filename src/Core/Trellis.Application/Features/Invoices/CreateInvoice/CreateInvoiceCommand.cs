using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Invoices.CreateInvoice;

public sealed record CreateInvoiceCommand(string CustomerName, string CustomerEmail, string Currency)
    : ICommand<InvoiceId>;
