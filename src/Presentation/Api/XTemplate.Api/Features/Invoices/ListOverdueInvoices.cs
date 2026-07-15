using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Api.Contracts.Common;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Authorization;
using XTemplate.Application.Features.Invoices;
using XTemplate.Application.Features.Invoices.ListOverdueInvoices;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Specifications;

namespace XTemplate.Api.Features.Invoices;

internal sealed class ListOverdueInvoices : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapGet("invoices/overdue", Handle)
        .RequirePermission(Permissions.Invoices.Read)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("List issued invoices past their due date, oldest first (paged)");

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
