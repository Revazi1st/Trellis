using Microsoft.AspNetCore.Http.HttpResults;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Hardening;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Auth;
using Trellis.Application.Features.Auth.Register;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Auth;

internal sealed class Register : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("auth/register", Handle)
        .AllowAnonymous()
        .RequireRateLimiting(ApiHardeningExtensions.AuthRateLimitPolicy)
        .WithTags(AuthEndpoints.Tag)
        .WithSummary("Create a new account")
        .WithDescription(
            "Creates a new account with the default member role. The account can sign in immediately; permissions come from its roles.");

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
