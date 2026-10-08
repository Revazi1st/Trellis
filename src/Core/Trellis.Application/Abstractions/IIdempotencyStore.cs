namespace Trellis.Application.Abstractions;

/// <summary>
/// The at-most-once guarantee behind the <c>Idempotency-Key</c> header (ADR-0018). A reservation is
/// tracked inside the command's unit of work, so it commits together with the aggregate and the outbox
/// or not at all: a failed command leaves no trace, and two first requests racing with one key resolve
/// at the primary key (<see cref="IdempotencyErrors.Duplicate"/> from the commit). The completed
/// request's response is stored against the reservation and replayed to a retry. Implemented in the
/// Persistence layer; the API's idempotency filter is the caller.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// What is known about <paramref name="key"/>: nothing, a reservation whose request is still running
    /// (or ended before it could store a response), or a completed request with its response.
    /// </summary>
    Task<IdempotencyLookup> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Tracks a reservation in the current unit of work. Nothing is written here: the reservation is
    /// persisted by the unit of work's commit, in the command's own transaction.
    /// </summary>
    void Reserve(string key, string requestFingerprint);

    /// <summary>
    /// Stores the response of the request that made this unit of work's reservation — a small second
    /// write after the commit. A reservation the unit of work never committed is dropped instead, so
    /// completing is always safe to call, whatever the outcome.
    /// </summary>
    Task CompleteAsync(string key, StoredResponse response, CancellationToken cancellationToken);
}

/// <summary>The three states a key can be in. Match on the nested types; <see cref="None"/> is the "never seen" case.</summary>
public abstract record IdempotencyLookup
{
    private IdempotencyLookup()
    {
    }

    /// <summary>No reservation exists for the key.</summary>
    public static IdempotencyLookup None { get; } = new Unknown();

    /// <summary>A reservation committed but no response was stored yet: the first request is still running, or died after its commit.</summary>
    public sealed record InFlight(DateTimeOffset ReservedAtUtc) : IdempotencyLookup;

    /// <summary>The request completed; <see cref="RequestFingerprint"/> tells a retry of the same request from a reuse of the key.</summary>
    public sealed record Completed(string RequestFingerprint, StoredResponse Response) : IdempotencyLookup;

    private sealed record Unknown : IdempotencyLookup;
}

/// <summary>
/// What a completed request answered. <see cref="Body"/> is null when the response was not stored
/// (it exceeded the replay cap, or the request ended in a failure after its command had committed):
/// the status, content type and <c>Location</c> are still known, but the response cannot be replayed.
/// </summary>
public sealed record StoredResponse(int StatusCode, string? ContentType, byte[]? Body, string? Location)
{
    public bool IsReplayable => Body is not null;

    /// <summary>A completion without a replayable body: only the fact that the request ended, and how, is kept.</summary>
    public static StoredResponse Unavailable(int statusCode, string? contentType = null, string? location = null)
        => new(statusCode, contentType, Body: null, location);
}
