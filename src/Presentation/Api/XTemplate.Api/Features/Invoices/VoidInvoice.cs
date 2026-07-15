using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Caching;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Application.Authorization;
using XTemplate.Application.Features.Invoices.VoidInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class VoidInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/void", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Void a draft or issued invoice");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        ICommandHandler<VoidInvoiceCommand> handler,
        HybridCache cache,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new VoidInvoiceCommand(id), cancellationToken);

        if (result.IsSuccess)
        {
            await cache.RemoveAsync(CacheKeys.Invoice(id.Value), cancellationToken);
            return TypedResults.NoContent();
        }

        return CustomResults.Problem(result.Error);
    }
}
