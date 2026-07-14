using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Invoices.PayInvoice;

public sealed record PayInvoiceCommand(InvoiceId InvoiceId) : ICommand;
