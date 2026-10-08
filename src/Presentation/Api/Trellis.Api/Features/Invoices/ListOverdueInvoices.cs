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

    private static async Task<Results<Ok<PagedResponse<InvoiceDto>>, ProblemHttpResult>> Handle(
        [AsParameters] PageRequest request,
        IQueryHandler<ListOverdueInvoicesQuery, PagedList<InvoiceResponse>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new ListOverdueInvoicesQuery(request.Page, request.PageSize), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(result.Value.ToPagedResponse(static i => i.ToDto()))
            : CustomResults.Problem(result.Error);
    }
}
