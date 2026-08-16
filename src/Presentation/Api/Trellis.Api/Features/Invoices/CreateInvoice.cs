using Microsoft.AspNetCore.Http.HttpResults;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Idempotency;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Invoices;

internal sealed class CreateInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .RequireIdempotency()
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
