using Microsoft.Extensions.Logging;

namespace Trellis.Persistence.Identity;

/// <summary>
/// Structured security events with stable event ids (1100–1199), so an alert or a SIEM rule can match
/// on the id instead of parsing a message. An event names the account by user id; a sign-in for an
/// e-mail that matches no account carries the masked e-mail (<see cref="MaskEmail"/>). None of them
/// ever carries a password.
/// </summary>
internal static partial class SecurityEventLog
{
    [LoggerMessage(EventId = 1101, EventName = "LoginFailed", Level = LogLevel.Information,
        Message = "Sign-in failed for user {UserId}: wrong password")]
    public static partial void LoginFailed(ILogger logger, Guid userId);

    /// <summary>Takes the e-mail as typed and logs only its masked form.</summary>
    public static void LoginFailedUnknownAccount(ILogger logger, string email)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            string maskedEmail = MaskEmail(email);
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
    /// <c>jane.doe@example.com</c> → <c>j***@example.com</c>: enough to spot a run against one domain,
    /// not enough to read back the address someone typed.
    /// </summary>
    private static string MaskEmail(string email)
    {
        int at = email.LastIndexOf('@');
        return at > 0 && at < email.Length - 1
            ? string.Concat(email.AsSpan(0, 1), "***", email.AsSpan(at))
            : "***";
    }
}
