namespace Trellis.Application.Abstractions;

/// <summary>
/// Records processed idempotency keys so a retried command is not applied twice.
/// Implemented in the Persistence layer.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>Atomically reserves the key. Returns false if it was already reserved (a duplicate request).</summary>
    Task<bool> TryReserveAsync(string key, CancellationToken cancellationToken);

    /// <summary>Releases a key so a genuinely-failed request can be retried with the same key.</summary>
    Task ReleaseAsync(string key, CancellationToken cancellationToken);
}
