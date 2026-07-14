namespace XTemplate.Application.Authorization;

/// <summary>Custom claim types shared across the auth stack (token minting, role seeding, authorization policies).</summary>
public static class AuthClaims
{
    /// <summary>A single granted permission; a principal carries one of these per permission it holds.</summary>
    public const string Permission = "permission";
}
