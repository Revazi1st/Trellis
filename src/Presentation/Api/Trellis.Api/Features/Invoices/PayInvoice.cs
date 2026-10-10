using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Caching;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Idempotency;
using Trellis.Api.Common.Results;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Invoices.PayInvoice;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Invoices;

internal sealed class PayInvoice : IEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/pay", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .RequireIdempotency()
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Record payment of an issued invoice")
        .WithDescription(
            "Records payment of an Issued invoice and moves it to Paid. Only an issued invoice can be paid.");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        ICommandHandler<PayInvoiceCommand> handler,
        CacheKeys cacheKeys,
        HybridCache cache,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new PayInvoiceCommand(id), cancellationToken);

        if (result.IsSuccess)
        {
            // Post-commit: an aborted request must not be able to throw here, or the idempotency filter
            // would release a key whose command already committed and a retry would apply it twice.
            await cache.RemoveAsync(cacheKeys.Invoice(id.Value), CancellationToken.None);
            return TypedResults.NoContent();
        }

        return CustomResults.Problem(result.Error);
    }
}
