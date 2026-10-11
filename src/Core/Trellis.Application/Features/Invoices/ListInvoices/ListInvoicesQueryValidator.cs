using FluentValidation;

namespace Trellis.Application.Features.Invoices.ListInvoices;

internal sealed class ListInvoicesQueryValidator : AbstractValidator<ListInvoicesQuery>
{
    // The same bounds the endpoint declares with [Range] (Api.Contracts.Common.PagingLimits), enforced
    // here so the document never promises a range the API does not hold; the page cap keeps OFFSET bounded.
    public ListInvoicesQueryValidator()
    {
        // A status the enum does not define (an ordinal the binder accepted, "?status=99") is refused
        // naming the field, rather than answering an empty list for a state that cannot exist.
        RuleFor(static q => q.Status).IsInEnum();
        RuleFor(static q => q.Page).InclusiveBetween(1, 10_000);
        RuleFor(static q => q.PageSize).InclusiveBetween(1, 100);
    }
}
