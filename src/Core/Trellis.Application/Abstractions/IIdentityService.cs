using Trellis.SharedKernel.Results;

namespace Trellis.Application.Abstractions;

/// <summary>Persistence-backed identity operations (ASP.NET Core Identity). Implemented in the Persistence layer.</summary>
public interface IIdentityService
{
    Task<Result<Guid>> RegisterAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>Verifies credentials and returns the authenticated user's identity plus effective permissions.</summary>
    Task<Result<AuthenticatedUser>> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken);

    Task<Result<AuthenticatedUser>> GetUserAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>An authenticated principal with the permissions aggregated from its roles.</summary>
public sealed record AuthenticatedUser(
    Guid Id,
    string Email,
    IReadOnlyList<string> Permissions
#if (UseMultitenancy)
    , string TenantId
#endif
    );
