using Microsoft.AspNetCore.Http.HttpResults;
using Trellis.Api.Common.Endpoints;
using Trellis.Api.Common.Results;
using Trellis.Application.Features.Auth.RevokeSessions;
using Trellis.SharedKernel.Messaging;

namespace Trellis.Api.Features.Auth;

internal sealed class RevokeSessions : IEndpoint
{
    public static void Map(IEndpointRouteBuilder app) => app
        .MapPost("auth/revoke-all", Handle)
        .RequireAuthorization()
        .WithTags(AuthEndpoints.Tag)
        .WithSummary("Sign out everywhere: invalidate all of the caller's refresh tokens (e.g. after a password change)");

    private static async Task<Results<NoContent, ProblemHttpResult>> Handle(
        ICommandHandler<RevokeSessionsCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RevokeSessionsCommand(), cancellationToken);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : CustomResults.Problem(result.Error);
    }
}
