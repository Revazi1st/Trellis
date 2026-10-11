using FluentValidation;
using Trellis.Domain.Invoices;

namespace Trellis.Application.Features.Invoices.AddInvoiceLine;

internal sealed class AddInvoiceLineCommandValidator : AbstractValidator<AddInvoiceLineCommand>
{
    public AddInvoiceLineCommandValidator()
    {
        RuleFor(static c => c.Description).NotEmpty().MaximumLength(InvoiceLine.DescriptionMaxLength);
        RuleFor(static c => c.Amount).GreaterThanOrEqualTo(0);
    }
}
