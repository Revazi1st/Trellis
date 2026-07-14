using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Features.Invoices.CreateInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class CreateInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices", Handle)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Create a draft invoice");

    private static async Task<Results<CreatedAtRoute<CreateInvoiceResponse>, ProblemHttpResult>> Handle(
        CreateInvoiceRequest request,
        ICommandHandler<CreateInvoiceCommand, InvoiceId> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new CreateInvoiceCommand(request.CustomerName, request.CustomerEmail, request.Currency),
            cancellationToken);

        return result.IsSuccess
            ? TypedResults.CreatedAtRoute(
                new CreateInvoiceResponse(result.Value.Value),
                InvoiceEndpoints.GetInvoiceRouteName,
                new { id = result.Value.Value })
            : CustomResults.Problem(result.Error);
    }
}
