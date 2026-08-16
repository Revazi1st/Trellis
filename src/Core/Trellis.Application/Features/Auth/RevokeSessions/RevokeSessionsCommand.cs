using Trellis.SharedKernel.Messaging;

namespace Trellis.Application.Features.Auth.RevokeSessions;

/// <summary>Signs the current user out everywhere by invalidating all of their refresh tokens.</summary>
public sealed record RevokeSessionsCommand : ICommand;
