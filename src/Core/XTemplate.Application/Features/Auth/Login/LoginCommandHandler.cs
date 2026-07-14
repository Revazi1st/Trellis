using XTemplate.Application.Abstractions;
using XTemplate.Application.Options;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Features.Auth.Login;

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
