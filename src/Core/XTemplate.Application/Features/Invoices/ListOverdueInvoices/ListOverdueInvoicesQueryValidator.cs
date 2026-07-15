using FluentValidation;

namespace XTemplate.Application.Features.Invoices.ListOverdueInvoices;

internal sealed class ListOverdueInvoicesQueryValidator : AbstractValidator<ListOverdueInvoicesQuery>
{
    public ListOverdueInvoicesQueryValidator()
    {
        RuleFor(static q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(static q => q.PageSize).InclusiveBetween(1, 100);
    }
}
