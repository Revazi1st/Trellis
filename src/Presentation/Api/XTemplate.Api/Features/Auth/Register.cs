using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Results;
using XTemplate.Api.Contracts.Auth;
using XTemplate.Application.Features.Auth.Register;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Auth;

internal sealed class Register : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("auth/register", Handle)
        .AllowAnonymous()
        .WithTags(AuthEndpoints.Tag)
        .WithSummary("Create a new account");

    private static async Task<Results<Ok<RegisterResponse>, ProblemHttpResult>> Handle(
        RegisterRequest request,
        ICommandHandler<RegisterCommand, Guid> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RegisterCommand(request.Email, request.Password), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(new RegisterResponse(result.Value))
            : CustomResults.Problem(result.Error);
    }
}
