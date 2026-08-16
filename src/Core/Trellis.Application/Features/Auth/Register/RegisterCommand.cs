using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Auth.Register;

public sealed record RegisterCommand(string Email, string Password) : ICommand<Guid>;
