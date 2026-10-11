using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Net.Http.Headers;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Caching;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.GetInvoice;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Invoices;

internal sealed class GetInvoice : IEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
        .MapGet("invoices/{id}", Handle)
        .RequirePermission(Permissions.Invoices.Read)
        .SupportsConditionalGet()
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Get one invoice with its lines")
        .WithDescription(
            "Returns one invoice with its lines and computed total. Responses are served through the read cache, which every write on the same invoice invalidates. The ETag is the version a write may name in If-Match.");

    private static async Task<Results<Ok<InvoiceDto>, StatusCodeHttpResult, ProblemHttpResult>> Handle(
        InvoiceId id,
        IQueryHandler<GetInvoiceQuery, InvoiceResponse> handler,
        CacheKeys cacheKeys,
        HybridCache cache,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Cache-aside: served from HybridCache (in-memory L1, optional Redis L2), invalidated by the write endpoints.
        string cacheKey = cacheKeys.Invoice(id.Value);
        InvoiceDto? dto = await cache.GetOrCreateAsync(
            cacheKey,
            async token =>
            {
                var result = await handler.Handle(new GetInvoiceQuery(id), token);
                return result.IsSuccess ? result.Value.ToDto() : null;
            },
            cancellationToken: cancellationToken);

        if (dto is not null)
        {
            // The xmin concurrency token doubles as a strong ETag: pollers pay a 304 instead of the
            // full aggregate when nothing changed, and a write sends it back as If-Match. Parsed, not
            // string-compared: the weak form, a list or "*" is honoured too (RFC 9110 §13.1.2).
            EntityTagHeaderValue etag = ConditionalGetExtensions.ETag(dto.Version);
            httpContext.Response.Headers.ETag = etag.ToString();

            // The opt-out from the API's no-store default (ADR-0021 amendment), on the 200 and the 304
            // alike: private keeps the representation out of shared caches, no-cache makes a browser
            // revalidate it with the ETag on every use instead of serving it blind. The 404 below sets
            // nothing, so it stays no-store like every other API response.
            httpContext.Response.Headers.CacheControl = "private, no-cache";

            return httpContext.Request.IsNotModified(etag)
                ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
                : TypedResults.Ok(dto);
        }

        // Never pin a miss: GetOrCreateAsync stored the null, and leaving it would keep answering 404
        // for the full TTL even after the invoice appears.
        await cache.RemoveAsync(cacheKey, cancellationToken);
        return CustomResults.Problem(InvoiceErrors.NotFound(id));
    }
}
