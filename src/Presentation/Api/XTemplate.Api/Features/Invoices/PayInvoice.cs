using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Application.Features.Invoices.PayInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class PayInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/pay", Handle)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Record payment of an issued invoice");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        ICommandHandler<PayInvoiceCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new PayInvoiceCommand(id), cancellationToken);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : CustomResults.Problem(result.Error);
    }
}
