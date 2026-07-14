using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Application.Authorization;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Features.Invoices;
using XTemplate.Application.Features.Invoices.ListOverdueInvoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class ListOverdueInvoices : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapGet("invoices/overdue", Handle)
        .RequirePermission(Permissions.Invoices.Read)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("List issued invoices past their due date, oldest first");

    private static async Task<Results<Ok<IReadOnlyList<InvoiceDto>>, ProblemHttpResult>> Handle(
        IQueryHandler<ListOverdueInvoicesQuery, IReadOnlyList<InvoiceResponse>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListOverdueInvoicesQuery(), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok<IReadOnlyList<InvoiceDto>>([.. result.Value.Select(static i => i.ToDto())])
            : CustomResults.Problem(result.Error);
    }
}
