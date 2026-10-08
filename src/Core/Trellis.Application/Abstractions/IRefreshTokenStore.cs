using Trellis.SharedKernel.Results;

namespace Trellis.Application.Abstractions;

/// <summary>
/// Persists rotating refresh tokens (hashed). Implemented in the Persistence layer. Like every port it
/// never commits on its own: <see cref="StoreAsync"/> joins the caller's unit of work. The consume and
/// revoke members are the declared exception — each is one atomic conditional UPDATE, which is what
/// makes concurrent replays race-safe.
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>
    /// Adds a new token's hash to the current unit of work; it is saved when the command commits (the
    /// UnitOfWork decorator), together with the rest of the command's writes.
    /// </summary>
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
