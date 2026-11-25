using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Trellis.Persistence.Identity;

/// <summary>
/// Structured security events with stable event ids (1100–1199), so an alert or a SIEM rule can match
/// on the id instead of parsing a message. An event names the account by user id; a sign-in for an
/// e-mail that matches no account carries the masked e-mail (<see cref="MaskEmail"/>), its line endings and
/// every other control character replaced by spaces. None of them ever carries a password, and none carries a
/// line break or a control character a caller typed: the masked e-mail is the only text here a caller chooses
/// (the rest are user ids and Identity's error codes). An event that takes a string is private behind such a
/// wrapper or names the reason no caller typed it in <c>SecurityEventLogTests</c> (ArchitectureTests).
/// </summary>
internal static partial class SecurityEventLog
{
    [LoggerMessage(EventId = 1101, EventName = "LoginFailed", Level = LogLevel.Information,
        Message = "Sign-in failed for user {UserId}: wrong password")]
    public static partial void LoginFailed(ILogger logger, Guid userId);

    /// <summary>
    /// Takes the e-mail as typed and logs only its masked form, on one line. Sign-in checks no e-mail format,
    /// so an anonymous caller can type a line break or another control character, and the mask keeps the domain
    /// and the first text element as typed: each line ending becomes a space here, or it would end the line early
    /// and let the caller write one of their own after it (CWE-117), and so does every other control character
    /// (a vertical tab or a file separator some log readers end a line at, an escape a terminal acts on). The mask
    /// itself stays as it is, a copy of Infrastructure's.
    /// </summary>
    public static void LoginFailedUnknownAccount(ILogger logger, string email)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            string maskedEmail = ControlCharactersAsSpaces(MaskEmail(email).ReplaceLineEndings(" "));
            LoginFailedUnknownAccountMasked(logger, maskedEmail);
        }
    }

    [LoggerMessage(EventId = 1102, EventName = "LoginFailedUnknownAccount", Level = LogLevel.Information,
        Message = "Sign-in failed for {MaskedEmail}: no such account")]
    private static partial void LoginFailedUnknownAccountMasked(ILogger logger, string maskedEmail);

    [LoggerMessage(EventId = 1103, EventName = "AccountLockedOut", Level = LogLevel.Warning,
        Message = "User {UserId} locked out after repeated failed sign-ins")]
    public static partial void AccountLockedOut(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 1104, EventName = "LoginRejectedLockedOut", Level = LogLevel.Information,
        Message = "Sign-in rejected for user {UserId}: account is locked out")]
    public static partial void LoginRejectedLockedOut(ILogger logger, Guid userId);

    /// <summary>
    /// Identity could not save a failed sign-in's count (usually a concurrent update of the same account),
    /// so the lockout did not see this attempt. Repeated, it is someone racing sign-ins to stay under the
    /// threshold.
    /// </summary>
    [LoggerMessage(EventId = 1105, EventName = "FailedSignInNotCounted", Level = LogLevel.Warning,
        Message = "A failed sign-in for user {UserId} was not counted toward lockout ({IdentityErrorCodes})")]
    public static partial void FailedSignInNotCounted(ILogger logger, Guid userId, string identityErrorCodes);

    [LoggerMessage(EventId = 1106, EventName = "FailedSignInCountNotReset", Level = LogLevel.Warning,
        Message = "The failed sign-in count for user {UserId} was not reset after a successful sign-in ({IdentityErrorCodes})")]
    public static partial void FailedSignInCountNotReset(ILogger logger, Guid userId, string identityErrorCodes);

    /// <summary>
    /// Every control character left after <see cref="string.ReplaceLineEndings(string)"/> (the sanitiser the analysers
    /// recognise, which also turns CR LF into one space and covers U+2028 and U+2029, no control characters) as a
    /// space: the rest of the C0 range, DEL and the C1 range.
    /// </summary>
    private static string ControlCharactersAsSpaces(string text)
        => string.Create(text.Length, text, static (chars, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                chars[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });

    /// <summary>
    /// <c>jane.doe@example.com</c> → <c>j***@example.com</c>: enough to spot a run against one domain,
    /// not enough to read back the address someone typed. The first character is kept as a reader sees it
    /// (a text element), never half of a surrogate pair. Infrastructure's <c>EmailAddressMask</c> is the
    /// same mask (the peers keep a copy each); <c>EmailAddressMaskTests</c> (ArchitectureTests) reaches
    /// both by name and holds them to the same rows.
    /// </summary>
    private static string MaskEmail(string email)
    {
        int at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1)
        {
            return "***";
        }

        ReadOnlySpan<char> localPart = email.AsSpan(0, at);
        return string.Concat(localPart[..StringInfo.GetNextTextElementLength(localPart)], "***", email.AsSpan(at));
    }
}
