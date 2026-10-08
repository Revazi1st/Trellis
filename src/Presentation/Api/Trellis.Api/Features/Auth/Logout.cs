using Microsoft.AspNetCore.Http.HttpResults;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Auth;
using Trellis.Application.Features.Auth.Logout;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Auth;

internal sealed class Logout : IEndpoint
{
    // Anonymous like Refresh: possession of the refresh token IS the authorization to revoke it,
    // and a caller whose access token already expired must still be able to log out.
    // Unguessable token: the global limiter bounds load; the strict auth policy is for guessable
    // credentials (login/register), ADR-0021.
    public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
        .MapPost("auth/logout", Handle)
        .AllowAnonymous()
        .WithTags(AuthEndpoints.Tag)
        .WithSummary("Invalidate a refresh token (sign out this session)")
        .WithDescription(
            "Invalidates a single refresh token. Idempotent, and it never triggers stolen-token replay detection — that is what separates a logout from presenting an already-consumed token.");

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
