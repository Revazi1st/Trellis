using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Auth.RefreshToken;

public sealed record RefreshTokenCommand(string RefreshToken) : ICommand<AuthTokens>;
