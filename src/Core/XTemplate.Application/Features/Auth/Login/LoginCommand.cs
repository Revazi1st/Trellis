using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Auth.Login;

public sealed record LoginCommand(string Email, string Password) : ICommand<AuthTokens>;
