using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Invoices.AddInvoiceLine;

/// <summary>The line is priced in the invoice's own currency.</summary>
public sealed record AddInvoiceLineCommand(InvoiceId InvoiceId, string Description, decimal Amount)
    : ICommand;
