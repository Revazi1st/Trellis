using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Invoices.VoidInvoice;

public sealed record VoidInvoiceCommand(InvoiceId InvoiceId) : ICommand;
