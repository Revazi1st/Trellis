using FluentValidation;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;

namespace Trellis.Application.Features.Invoices.CreateInvoice;

/// <summary>
/// Shape checks only — semantic rules (email format, ISO currency) live in the domain value objects. The
/// lengths are the Domain's constants, so the validator, the column and the domain's own check agree.
/// </summary>
internal sealed class CreateInvoiceCommandValidator : AbstractValidator<CreateInvoiceCommand>
{
    public CreateInvoiceCommandValidator()
    {
        RuleFor(static c => c.CustomerName).NotEmpty().MaximumLength(Invoice.CustomerNameMaxLength);
        RuleFor(static c => c.CustomerEmail).NotEmpty().MaximumLength(EmailAddress.MaxLength);
        RuleFor(static c => c.Currency).NotEmpty().Length(Currency.CodeLength);
    }
}
