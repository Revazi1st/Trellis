using Trellis.Application.Abstractions;

namespace Trellis.Persistence.Idempotency;

/// <summary>
/// One reservation per <c>Idempotency-Key</c> (hashed with the caller, method and route); the primary
/// key enforces at-most-once. The row is inserted by the command's own commit and, once the response
/// has gone out, completed with what was sent so a retry can be answered without running the command
/// again. A row with no <see cref="StatusCode"/> is in flight. The janitor sweeps rows by
/// <see cref="CreatedAtUtc"/>, whatever their state (<c>Retention:IdempotencyKeyAge</c>).
/// </summary>
internal sealed class IdempotencyKey
{
    public required string Key { get; init; }

    /// <summary>SHA-256 (hex) of method, path and body: a retry must be the same request, not merely the same key.</summary>
    public string RequestFingerprint { get; init; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; init; }

    public int? StatusCode { get; private set; }

    public string? ContentType { get; private set; }

    /// <summary>The response body as sent; null once completed means it was not stored (replay cap, or a failure after the commit).</summary>
    public byte[]? Body { get; private set; }

    public string? Location { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public bool IsCompleted => StatusCode is not null;

    public void Complete(StoredResponse response, DateTimeOffset now)
    {
        StatusCode = response.StatusCode;
        ContentType = response.ContentType;
        Body = response.Body;
        Location = response.Location;
        CompletedAtUtc = now;
    }

    public StoredResponse ToStoredResponse()
        => new(StatusCode ?? throw new InvalidOperationException("The reservation has not completed."), ContentType, Body, Location);
}
