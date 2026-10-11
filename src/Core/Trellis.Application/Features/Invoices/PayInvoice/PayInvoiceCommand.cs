using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Invoices.PayInvoice;

/// <summary><paramref name="ExpectedVersion"/>, when given, is the invoice version the caller read (<see cref="InvoicePreconditions"/>).</summary>
public sealed record PayInvoiceCommand(InvoiceId InvoiceId, uint? ExpectedVersion = null) : ICommand;
