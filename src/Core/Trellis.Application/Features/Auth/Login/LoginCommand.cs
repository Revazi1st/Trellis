using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Auth.Login;

public sealed record LoginCommand(string Email, string Password) : ICommand<AuthTokens>;
