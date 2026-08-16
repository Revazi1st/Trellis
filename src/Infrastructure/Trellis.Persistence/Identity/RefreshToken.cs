namespace Trellis.Persistence.Identity;

/// <summary>A persisted, hashed, single-use refresh token. Plain tokens are never stored.</summary>
internal sealed class RefreshToken
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public required string TokenHash { get; init; }

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public DateTimeOffset? ConsumedAtUtc { get; set; }
}
