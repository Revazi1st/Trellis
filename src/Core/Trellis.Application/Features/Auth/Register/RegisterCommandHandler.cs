using Trellis.Application.Abstractions;
using Trellis.Application.Options;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Auth.Register;

/// <summary>
/// The single place a self-service account is created. Everything that comes with an account — the
/// Member role today, a freshly provisioned tenant under the multi-tenancy option (inside
/// <see cref="IIdentityService.RegisterAsync"/>) — sits behind the <see cref="RegistrationOptions.Mode"/>
/// check below, so a closed deployment creates nothing.
/// </summary>
internal sealed class RegisterCommandHandler(
    IIdentityService identityService,
    RegistrationOptions options) : ICommandHandler<RegisterCommand, Guid>
{
    public Task<Result<Guid>> Handle(RegisterCommand command, CancellationToken cancellationToken)
    {
        if (options.Mode != RegistrationMode.Open)
        {
            return Task.FromResult(Result.Failure<Guid>(AuthErrors.RegistrationClosed));
        }

        return identityService.RegisterAsync(command.Email, command.Password, cancellationToken);
    }
}
