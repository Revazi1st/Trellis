using FluentValidation;

namespace Trellis.Application.Features.Invoices.ListOverdueInvoices;

internal sealed class ListOverdueInvoicesQueryValidator : AbstractValidator<ListOverdueInvoicesQuery>
{
    // The same bounds the endpoint declares with [Range] (Api.Contracts.Common.PagingLimits), enforced
    // here so the document never promises a range the API does not hold; the page cap keeps OFFSET bounded.
    public ListOverdueInvoicesQueryValidator()
    {
        RuleFor(static q => q.Page).InclusiveBetween(1, 10_000);
        RuleFor(static q => q.PageSize).InclusiveBetween(1, 100);
    }
}
