using XTemplate.Application.Abstractions;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Features.Auth.Register;

internal sealed class RegisterCommandHandler(IIdentityService identityService)
    : ICommandHandler<RegisterCommand, Guid>
{
    public Task<Result<Guid>> Handle(RegisterCommand command, CancellationToken cancellationToken)
        => identityService.RegisterAsync(command.Email, command.Password, cancellationToken);
}
