using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Auth.Logout;

public sealed record LogoutCommand(string RefreshToken) : ICommand;
