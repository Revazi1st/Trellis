using FluentValidation;

namespace XTemplate.Application.Features.Invoices.AddInvoiceLine;

internal sealed class AddInvoiceLineCommandValidator : AbstractValidator<AddInvoiceLineCommand>
{
    public AddInvoiceLineCommandValidator()
    {
        RuleFor(static c => c.Description).NotEmpty().MaximumLength(500);
        RuleFor(static c => c.Amount).GreaterThanOrEqualTo(0);
    }
}
