using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Authentication;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Application.Authorization;
using XTemplate.Api.Contracts.Invoices;
using XTemplate.Application.Features.Invoices.IssueInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Invoices;

internal sealed class IssueInvoice : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("invoices/{id}/issue", Handle)
        .RequirePermission(Permissions.Invoices.Write)
        .WithTags(InvoiceEndpoints.Tag)
        .WithSummary("Issue a draft invoice, making it payable");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        InvoiceId id,
        IssueInvoiceRequest request,
        ICommandHandler<IssueInvoiceCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new IssueInvoiceCommand(id, request.DueDate), cancellationToken);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : CustomResults.Problem(result.Error);
    }
}
