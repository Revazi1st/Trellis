using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Auth.Register;

public sealed record RegisterCommand(string Email, string Password) : ICommand<Guid>;
