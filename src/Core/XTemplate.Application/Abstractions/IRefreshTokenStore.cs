using XTemplate.SharedKernel.Results;

namespace XTemplate.Application.Abstractions;

/// <summary>Persists rotating refresh tokens (hashed). Implemented in the Persistence layer.</summary>
public interface IRefreshTokenStore
{
    Task StoreAsync(Guid userId, string tokenHash, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken);

    /// <summary>Atomically validates and consumes a refresh token (single-use rotation); returns the owning user id.</summary>
    Task<Result<Guid>> ConsumeAsync(string tokenHash, CancellationToken cancellationToken);
}
