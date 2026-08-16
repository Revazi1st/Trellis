using Microsoft.AspNetCore.Http.HttpResults;
using XTemplate.Api.Common.Endpoints;
using XTemplate.Api.Common.Hardening;
using XTemplate.Api.Common.Results;
using XTemplate.Api.Contracts.Auth;
using XTemplate.Application.Features.Auth.Logout;
using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Api.Features.Auth;

internal sealed class Logout : IEndpoint
{
    // Anonymous like Refresh: possession of the refresh token IS the authorization to revoke it,
    // and a caller whose access token already expired must still be able to log out.
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("auth/logout", Handle)
        .AllowAnonymous()
        .RequireRateLimiting(ApiHardeningExtensions.AuthRateLimitPolicy)
        .WithTags(AuthEndpoints.Tag)
        .WithSummary("Invalidate a refresh token (sign out this session)");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        LogoutRequest request,
        ICommandHandler<LogoutCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new LogoutCommand(request.RefreshToken), cancellationToken);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : CustomResults.Problem(result.Error);
    }
}
