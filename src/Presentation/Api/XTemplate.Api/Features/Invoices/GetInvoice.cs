using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Caching;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Authorization;
using XTemplate.Application.Features.Invoices;
using XTemplate.Application.Features.Invoices.GetInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class GetInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapGet("invoices/{id}", Handle)
        .WithName(InvoiceEndpoints.GetInvoiceRouteName)
        .RequirePermission(Permissions.Invoices.Read)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Get one invoice with its lines");

    private static async Task<Results<Ok<InvoiceDto>, ProblemHttpResult>> Handle(
        InvoiceId id,
        IQueryHandler<GetInvoiceQuery, InvoiceResponse> handler,
        HybridCache cache,
        CancellationToken cancellationToken)
    {
        // Cache-aside: served from HybridCache (in-memory L1, optional Redis L2), invalidated by the write endpoints.
        InvoiceDto? dto = await cache.GetOrCreateAsync(
            CacheKeys.Invoice(id.Value),
            async token =>
            {
                var result = await handler.Handle(new GetInvoiceQuery(id), token);
                return result.IsSuccess ? result.Value.ToDto() : null;
            },
            cancellationToken: cancellationToken);

        return dto is not null
            ? TypedResults.Ok(dto)
            : CustomResults.Problem(InvoiceErrors.NotFound(id));
    }
}
