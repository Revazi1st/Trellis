using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.ListInvoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Specifications;

namespace Trellis.Api.Features.Invoices;

internal sealed class ListInvoices : IEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
        .MapGet("invoices", Handle)
        .RequirePermission(Permissions.Invoices.Read)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("List invoices, newest first (paged, optionally by status)")
        .WithDescription(
            "Every invoice from the moment it is created, newest first; status narrows the list to Draft, Issued, Paid or Voided invoices. Paged via the standard page/pageSize parameters; the response is the PagedResponse envelope, never a bare array.");

    // The paging parameters are inline, camelCase and bounded: that is how the document gets their names
    // and ranges (ADR-0025). The attributes document; the query's validator enforces the same bounds. The
    // status is the Contracts enum, so the document names its values and a value outside them is a 400.
    private static async Task<Results<Ok<PagedResponse<InvoiceDto>>, ProblemHttpResult>> Handle(
        IQueryHandler<ListInvoicesQuery, PagedList<InvoiceResponse>> handler,
        CancellationToken cancellationToken,
        InvoiceStatus? status = null,
        [Range(PagingLimits.MinPage, PagingLimits.MaxPage)] int page = PagingLimits.MinPage,
        [Range(PagingLimits.MinPageSize, PagingLimits.MaxPageSize)] int pageSize = PagingLimits.DefaultPageSize)
    {
        var result = await handler.Handle(new ListInvoicesQuery(status?.ToDomain(), page, pageSize), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(result.Value.ToPagedResponse(static i => i.ToDto()))
            : CustomResults.Problem(result.Error);
    }
}
