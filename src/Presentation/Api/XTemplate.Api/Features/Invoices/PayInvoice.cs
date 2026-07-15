using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Caching;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Application.Authorization;
using XTemplate.Application.Features.Invoices.PayInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class PayInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/pay", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Record payment of an issued invoice");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        ICommandHandler<PayInvoiceCommand> handler,
        HybridCache cache,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new PayInvoiceCommand(id), cancellationToken);

        if (result.IsSuccess)
        {
            await cache.RemoveAsync(CacheKeys.Invoice(id.Value), cancellationToken);
            return TypedResults.NoContent();
        }

        return CustomResults.Problem(result.Error);
    }
}
