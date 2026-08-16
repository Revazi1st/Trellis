using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Caching;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Idempotency;
using Trellis.Api.Common.Results;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Invoices.VoidInvoice;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Invoices;

internal sealed class VoidInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/void", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .RequireIdempotency()
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Void a draft or issued invoice");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        ICommandHandler<VoidInvoiceCommand> handler,
        CacheKeys cacheKeys,
        HybridCache cache,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new VoidInvoiceCommand(id), cancellationToken);

        if (result.IsSuccess)
        {
            await cache.RemoveAsync(cacheKeys.Invoice(id.Value), cancellationToken);
            return TypedResults.NoContent();
        }

        return CustomResults.Problem(result.Error);
    }
}
