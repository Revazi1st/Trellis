using Trellis.Application.Abstractions;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Auth.Register;

internal sealed class RegisterCommandHandler(IIdentityService identityService)
    : ICommandHandler<RegisterCommand, Guid>
{
    public Task<Result<Guid>> Handle(RegisterCommand command, CancellationToken cancellationToken)
        => identityService.RegisterAsync(command.Email, command.Password, cancellationToken);
}
