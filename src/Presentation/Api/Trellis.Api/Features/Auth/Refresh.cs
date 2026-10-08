using Microsoft.AspNetCore.Http.HttpResults;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Auth;
using Trellis.Application.Features.Auth.RefreshToken;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Auth;

internal sealed class Refresh : IEndpoint
{
    // Unguessable token: the global limiter bounds load; the strict auth policy is for guessable
    // credentials (login/register). Keeping refresh off it means a shared source (a Blazor Server
    // host refreshing for many users) never turns a silent refresh into a 429 (ADR-0021).
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
        .MapPost("auth/refresh", Handle)
        .AllowAnonymous()
        .WithTags(AuthEndpoints.Tag)
        .WithSummary("Rotate a refresh token for a new token pair")
        .WithDescription(
            "Exchanges a refresh token for a new token pair. Refresh tokens are single-use: presenting a consumed one is treated as a replay and revokes every outstanding token for that user.");

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> Handle(
        RefreshRequest request,
        ICommandHandler<RefreshTokenCommand, Trellis.Application.Features.Auth.AuthTokens> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RefreshTokenCommand(request.RefreshToken), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Ok(new TokenResponse(result.Value.AccessToken, result.Value.RefreshToken, result.Value.ExpiresInSeconds))
            : CustomResults.Problem(result.Error);
    }
}
