using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Auth.RefreshToken;

public sealed record RefreshTokenCommand(string RefreshToken) : ICommand<AuthTokens>;
