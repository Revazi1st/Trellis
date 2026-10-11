using Trellis.Domain.Common;

namespace Trellis.Application.Features.Auth;

/// <summary>
/// Shape limits shared by the auth validators. They exist so nothing unbounded reaches Identity's
/// lookups, the password hasher or the refresh-token hash — a null field or a megabyte "password"
/// is a 400, never a 500 or a slow hash.
/// </summary>
internal static class AuthInputLimits
{
    /// <summary>
    /// The Domain's bound (254: RFC 5321's forward-path limit less the brackets, with a 64-character
    /// local part). It is also within Identity's 256-character <c>Email</c>/<c>UserName</c> columns, so
    /// an address the validator accepts can always be stored: the 320 this used to be let a longer
    /// address past the validator only to fail the INSERT, a 500 instead of a 400.
    /// </summary>
    public const int EmailMaxLength = EmailAddress.MaxLength;

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
