using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Hybrid;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Caching;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Idempotency;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.AddInvoiceLine;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Invoices;

internal sealed class AddInvoiceLine : IEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/lines", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .RequireIdempotency()
        .SupportsConditionalWrite()
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Add a line to a draft invoice")
        .WithDescription(
            "Appends a line to a Draft invoice. Lines can only be changed while the invoice is a draft, and the line currency must match the invoice currency; both are rejected with a 409/400 problem document rather than an exception. Send If-Match with the ETag from GET to act only on the version you read: a stale one is a 412 and changes nothing.");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        AddInvoiceLineRequest request,
        ICommandHandler<AddInvoiceLineCommand> handler,
        CacheKeys cacheKeys,
        HybridCache cache,
        HttpRequest httpRequest,
        CancellationToken cancellationToken)
    {
        if (!httpRequest.TryGetExpectedVersion(out uint? expectedVersion))
        {
            return CustomResults.Problem(InvoicePreconditions.VersionMismatch);
        }

        var result = await handler.Handle(
            new AddInvoiceLineCommand(id, request.Description, request.Amount, expectedVersion),
            cancellationToken);

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
