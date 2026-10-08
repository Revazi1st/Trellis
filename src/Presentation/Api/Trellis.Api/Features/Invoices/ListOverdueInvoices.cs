using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.ListOverdueInvoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Specifications;

namespace Trellis.Api.Features.Invoices;

internal sealed class ListOverdueInvoices : IEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
        .MapGet("invoices/overdue", Handle)
        .RequirePermission(Permissions.Invoices.Read)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("List issued invoices past their due date, oldest first (paged)")
        .WithDescription(
            "Issued invoices whose due date has passed, oldest first. Paged via the standard page/pageSize parameters; the response is the PagedResponse envelope, never a bare array.");

    // The paging parameters are inline, camelCase and bounded: that is how the document gets their names
    // and ranges (ADR-0025). The attributes document; the query's validator enforces the same bounds.
    private static async Task<Results<Ok<PagedResponse<InvoiceDto>>, ProblemHttpResult>> Handle(
        IQueryHandler<ListOverdueInvoicesQuery, PagedList<InvoiceResponse>> handler,
        CancellationToken cancellationToken,
        [Range(PagingLimits.MinPage, PagingLimits.MaxPage)] int page = PagingLimits.MinPage,
        [Range(PagingLimits.MinPageSize, PagingLimits.MaxPageSize)] int pageSize = PagingLimits.DefaultPageSize)
    {
        var result = await handler.Handle(new ListOverdueInvoicesQuery(page, pageSize), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(result.Value.ToPagedResponse(static i => i.ToDto()))
            : CustomResults.Problem(result.Error);
    }
}
