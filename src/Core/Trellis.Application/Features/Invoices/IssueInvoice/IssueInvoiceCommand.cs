using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Invoices.IssueInvoice;

/// <summary><paramref name="ExpectedVersion"/>, when given, is the invoice version the caller read (<see cref="InvoicePreconditions"/>).</summary>
public sealed record IssueInvoiceCommand(InvoiceId InvoiceId, DateTimeOffset DueDate, uint? ExpectedVersion = null) : ICommand;
