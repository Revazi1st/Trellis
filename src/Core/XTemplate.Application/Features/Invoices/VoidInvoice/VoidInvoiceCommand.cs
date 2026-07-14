using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Invoices.VoidInvoice;

public sealed record VoidInvoiceCommand(InvoiceId InvoiceId) : ICommand;
