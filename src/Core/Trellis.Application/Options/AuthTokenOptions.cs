using System.ComponentModel.DataAnnotations;

namespace Trellis.Application.Options;

/// <summary>
/// Token lifetimes and signing configuration, bound from the "Authentication" configuration section.
/// The annotations are enforced at startup by <c>ValidateDataAnnotations().ValidateOnStart()</c> in
/// <c>AddInfrastructure</c>, so a missing or too-short signing key stops the host at boot instead of
/// failing the first login with an opaque IDX10653 from deep inside the token stack.
/// </summary>
public sealed class AuthTokenOptions
{
    public const string SectionName = "Authentication";

    /// <summary>Minimum signing-key length: HMAC-SHA256 needs 256 bits of key material.</summary>
    public const int MinimumSigningKeyLength = 32;

    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; init; } = "trellis";

    [Required(AllowEmptyStrings = false)]
    public string Audience { get; init; } = "trellis";

    /// <summary>HMAC-SHA256 signing key. Supply via configuration/secret in every environment — never commit one.</summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(MinimumSigningKeyLength)]
    public string SigningKey { get; init; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; init; } = 15;

    [Range(1, 365)]
    public int RefreshTokenDays { get; init; } = 7;
}
