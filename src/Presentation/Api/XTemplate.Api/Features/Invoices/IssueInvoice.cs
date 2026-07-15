using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Caching;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Idempotency;
using XTemplate.Api.Common.Results;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Authorization;
using XTemplate.Application.Features.Invoices.IssueInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class IssueInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/issue", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .RequireIdempotency()
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Issue a draft invoice, making it payable");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        IssueInvoiceRequest request,
        ICommandHandler<IssueInvoiceCommand> handler,
        HybridCache cache,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new IssueInvoiceCommand(id, request.DueDate), cancellationToken);

        if (result.IsSuccess)
        {
            await cache.RemoveAsync(CacheKeys.Invoice(id.Value), cancellationToken);
            return TypedResults.NoContent();
        }

        return CustomResults.Problem(result.Error);
    }
}
