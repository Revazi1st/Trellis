using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Caching;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Idempotency;
using XTemplate.Api.Common.Results;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Authorization;
using XTemplate.Application.Features.Invoices.AddInvoiceLine;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class AddInvoiceLine : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/lines", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .RequireIdempotency()
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Add a line to a draft invoice");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        AddInvoiceLineRequest request,
        ICommandHandler<AddInvoiceLineCommand> handler,
        CacheKeys cacheKeys,
        HybridCache cache,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new AddInvoiceLineCommand(id, request.Description, request.Amount),
            cancellationToken);

        if (result.IsSuccess)
        {
            await cache.RemoveAsync(cacheKeys.Invoice(id.Value), cancellationToken);
            return TypedResults.NoContent();
        }

        return CustomResults.Problem(result.Error);
    }
}
