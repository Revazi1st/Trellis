using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Application.Features.Invoices.VoidInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class VoidInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/void", Handle)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Void a draft or issued invoice");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        ICommandHandler<VoidInvoiceCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new VoidInvoiceCommand(id), cancellationToken);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : CustomResults.Problem(result.Error);
    }
}
