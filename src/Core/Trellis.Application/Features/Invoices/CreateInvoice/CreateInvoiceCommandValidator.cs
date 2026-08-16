using FluentValidation;

namespace Trellis.Application.Features.Invoices.CreateInvoice;

/// <summary>Shape checks only — semantic rules (email format, ISO currency) live in the domain value objects.</summary>
internal sealed class CreateInvoiceCommandValidator : AbstractValidator<CreateInvoiceCommand>
{
    public CreateInvoiceCommandValidator()
    {
        RuleFor(static c => c.CustomerName).NotEmpty().MaximumLength(200);
        RuleFor(static c => c.CustomerEmail).NotEmpty().MaximumLength(320);
        RuleFor(static c => c.Currency).NotEmpty().Length(3);
    }
}
