using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Abstractions;

/// <summary>Persists rotating refresh tokens (hashed). Implemented in the Persistence layer.</summary>
public interface IRefreshTokenStore
{
    Task StoreAsync(Guid userId, string tokenHash, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically validates and consumes a refresh token (single-use rotation); returns the owning
    /// user id. Presenting an already-consumed token is treated as a stolen-token replay and revokes
    /// every outstanding token of that user (OAuth security BCP).
    /// </summary>
    Task<Result<Guid>> ConsumeAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Invalidates one token if it is still live. Idempotent; never triggers replay detection (logout).</summary>
    Task RevokeAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>Invalidates every live token of the user (sign out everywhere); returns how many were revoked.</summary>
    Task<int> RevokeAllAsync(Guid userId, CancellationToken cancellationToken);
}
