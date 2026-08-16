using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Invoices.IssueInvoice;

public sealed record IssueInvoiceCommand(InvoiceId InvoiceId, DateTimeOffset DueDate) : ICommand;
