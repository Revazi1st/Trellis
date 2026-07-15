using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Hardening;
using XTemplate.Api.Common.Results;
using XTemplate.Api.Contracts.Auth;
using XTemplate.Application.Features.Auth.Login;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Auth;

internal sealed class Login : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("auth/login", Handle)
        .AllowAnonymous()
        .RequireRateLimiting(ApiHardeningExtensions.AuthRateLimitPolicy)
        .WithTags(AuthEndpoints.Tag)
        .WithSummary("Exchange credentials for an access + refresh token pair");

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> Handle(
        LoginRequest request,
        ICommandHandler<LoginCommand, XTemplate.Application.Features.Auth.AuthTokens> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new LoginCommand(request.Email, request.Password), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(new TokenResponse(result.Value.AccessToken, result.Value.RefreshToken, result.Value.ExpiresInSeconds))
            : CustomResults.Problem(result.Error);
    }
}
