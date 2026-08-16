using Trellis.Application.Abstractions;
using Trellis.Application.Options;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Auth.Login;

internal sealed class LoginCommandHandler(
    IIdentityService identityService,
    ITokenProvider tokenProvider,
    IRefreshTokenStore refreshTokenStore,
    TimeProvider timeProvider,
    AuthTokenOptions options) : ICommandHandler<LoginCommand, AuthTokens>
{
    public async Task<Result<AuthTokens>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        Result<AuthenticatedUser> user = await identityService.ValidateCredentialsAsync(
            command.Email, command.Password, cancellationToken);

        if (user.IsFailure)
        {
            return Result.Failure<AuthTokens>(user.Error);
        }

        return await TokenIssuer.IssueAsync(
            user.Value, tokenProvider, refreshTokenStore, timeProvider, options, cancellationToken);
    }
}
