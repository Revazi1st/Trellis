using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Invoices.IssueInvoice;

public sealed record IssueInvoiceCommand(InvoiceId InvoiceId, DateTimeOffset DueDate) : ICommand;
