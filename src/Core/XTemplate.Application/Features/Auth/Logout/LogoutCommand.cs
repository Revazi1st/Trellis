using XTemplate.SharedKernel.Messaging;

namespace XTemplate.Application.Features.Auth.Logout;

public sealed record LogoutCommand(string RefreshToken) : ICommand;
