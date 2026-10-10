namespace Trellis.Application.Features.Auth;

/// <summary>
/// Shape limits shared by the auth validators. They exist so nothing unbounded reaches Identity's
/// lookups, the password hasher or the refresh-token hash — a null field or a megabyte "password"
/// is a 400, never a 500 or a slow hash.
/// </summary>
internal static class AuthInputLimits
{
    /// <summary>RFC 5321's practical maximum for an address (64-character local part, 255-character domain).</summary>
    public const int EmailMaxLength = 320;

    public const int PasswordMinLength = 8;

    /// <summary>Applies at login as well as registration, so no accepted password is longer than this.</summary>
    public const int PasswordMaxLength = 128;

    /// <summary>
    /// Ceiling for an opaque refresh token. <c>JwtTokenProvider</c> issues 43 characters (32 random bytes,
    /// base64url); the head-room is for a replacement <c>ITokenProvider</c> with a longer format, while
    /// still bounding what gets hashed.
    /// </summary>
    public const int RefreshTokenMaxLength = 512;
}
