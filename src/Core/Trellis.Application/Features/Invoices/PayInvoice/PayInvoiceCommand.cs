using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Invoices.PayInvoice;

public sealed record PayInvoiceCommand(InvoiceId InvoiceId) : ICommand;
