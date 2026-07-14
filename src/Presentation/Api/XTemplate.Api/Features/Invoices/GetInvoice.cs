using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Application.Authorization;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Features.Invoices;
using XTemplate.Application.Features.Invoices.GetInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class GetInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapGet("invoices/{id}", Handle)
        .WithName(InvoiceEndpoints.GetInvoiceRouteName)
        .RequirePermission(Permissions.Invoices.Read)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Get one invoice with its lines");

    private static async Task<Results<Ok<InvoiceDto>, ProblemHttpResult>> Handle(
        InvoiceId id,
        IQueryHandler<GetInvoiceQuery, InvoiceResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetInvoiceQuery(id), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(result.Value.ToDto())
            : CustomResults.Problem(result.Error);
    }
}
