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
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        // The one write whose accidental repeat looks like a second intent: two drafts, not one. So the key
        // is required here, not merely honoured; the other writes act on an addressed invoice.
        .RequireIdempotency(keyRequired: true)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Create a draft invoice")
        .WithDescription(
            "Creates an invoice in Draft state with no lines. The currency is fixed at creation and every line added later must match it. An Idempotency-Key header is required, so a retried request cannot create a second invoice.");

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
                nameof(GetInvoice),
                new { id = result.Value.Value })
            : CustomResults.Problem(result.Error);
    }
}
