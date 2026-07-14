namespace XTemplate.Application.Options;

/// <summary>Token lifetimes and signing configuration, bound from the "Authentication" configuration section.</summary>
public sealed class AuthTokenOptions
{
    public const string SectionName = "Authentication";

    public string Issuer { get; init; } = "xtemplate";

    public string Audience { get; init; } = "xtemplate";

    /// <summary>HMAC-SHA256 signing key. Supply via configuration/secret in every environment.</summary>
    public string SigningKey { get; init; } = string.Empty;

    public int AccessTokenMinutes { get; init; } = 15;

    public int RefreshTokenDays { get; init; } = 7;
}
