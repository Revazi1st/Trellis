using Microsoft.AspNetCore.Http.HttpResults;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Hardening;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Auth;
using Trellis.Application.Features.Auth.Login;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Auth;

internal sealed class Login : IEndpoint
{
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
        .MapPost("auth/login", Handle)
        .AllowAnonymous()
        .RequireRateLimiting(ApiHardeningExtensions.AuthRateLimitPolicy)
        .WithTags(AuthEndpoints.Tag)
        .WithSummary("Exchange credentials for an access + refresh token pair")
        .WithDescription(
            "Validates credentials and returns a short-lived access token plus a single-use refresh token. Rate limited more strictly than the rest of the API.");

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> Handle(
        LoginRequest request,
        ICommandHandler<LoginCommand, Trellis.Application.Features.Auth.AuthTokens> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new LoginCommand(request.Email, request.Password), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(new TokenResponse(result.Value.AccessToken, result.Value.RefreshToken, result.Value.ExpiresInSeconds))
            : CustomResults.Problem(result.Error);
    }
}
