using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Invoices.AddInvoiceLine;

/// <summary>
/// The line is priced in the invoice's own currency. <paramref name="ExpectedVersion"/>, when given, is
/// the invoice version the caller read; a newer one fails the command (<see cref="InvoicePreconditions"/>).
/// </summary>
public sealed record AddInvoiceLineCommand(InvoiceId InvoiceId, string Description, decimal Amount, uint? ExpectedVersion = null)
    : ICommand;
