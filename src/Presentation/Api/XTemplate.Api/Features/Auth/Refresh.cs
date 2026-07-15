using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Hardening;
using XTemplate.Api.Common.Results;
using XTemplate.Api.Contracts.Auth;
using XTemplate.Application.Features.Auth.RefreshToken;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Auth;

internal sealed class Refresh : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("auth/refresh", Handle)
        .AllowAnonymous()
        .RequireRateLimiting(ApiHardeningExtensions.AuthRateLimitPolicy)
        .WithTags(AuthEndpoints.Tag)
        .WithSummary("Rotate a refresh token for a new token pair");

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> Handle(
        RefreshRequest request,
        ICommandHandler<RefreshTokenCommand, XTemplate.Application.Features.Auth.AuthTokens> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RefreshTokenCommand(request.RefreshToken), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(new TokenResponse(result.Value.AccessToken, result.Value.RefreshToken, result.Value.ExpiresInSeconds))
            : CustomResults.Problem(result.Error);
    }
}
