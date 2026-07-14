using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Application.Authorization;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Features.Invoices.AddInvoiceLine;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class AddInvoiceLine : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/lines", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Add a line to a draft invoice");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        AddInvoiceLineRequest request,
        ICommandHandler<AddInvoiceLineCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new AddInvoiceLineCommand(id, request.Description, request.Amount),
            cancellationToken);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : CustomResults.Problem(result.Error);
    }
}
