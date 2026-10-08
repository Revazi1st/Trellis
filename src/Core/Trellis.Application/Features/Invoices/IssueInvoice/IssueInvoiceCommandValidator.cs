using FluentValidation;

namespace Trellis.Application.Features.Invoices.IssueInvoice;

/// <summary>
/// Shape check only: a body without a due date binds to <c>default(DateTimeOffset)</c>, which is
/// answered here. "Not in the past" needs the clock, so it is the aggregate's rule (Invoice.Issue).
/// </summary>
internal sealed class IssueInvoiceCommandValidator : AbstractValidator<IssueInvoiceCommand>
{
    public IssueInvoiceCommandValidator()
    {
        RuleFor(static c => c.DueDate).NotEmpty().WithMessage("A due date is required.");
    }
}
