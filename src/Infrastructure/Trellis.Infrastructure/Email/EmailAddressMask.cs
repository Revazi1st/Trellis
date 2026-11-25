using System.Globalization;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// The masked form of an e-mail address for logs: <c>jane.doe@example.com</c> → <c>j***@example.com</c>.
/// Enough to tell which domain a message went to, not enough to read the address back. The same shape
/// Persistence's security events use (ADR-0021); the two projects are peers, so each keeps its copy.
/// <para>
/// The local part keeps its first character as a reader sees it (a text element), never half of a surrogate
/// pair: a first character outside the Basic Multilingual Plane is two UTF-16 code units, and one of them alone
/// is no character at all (UTF-8 cannot encode it, and the outbox stores it as U+FFFD). Persistence's copy is
/// <c>SecurityEventLog.MaskEmail</c>; <c>EmailAddressMaskTests</c> (ArchitectureTests) holds both to the same rows.
/// </para>
/// </summary>
internal static class EmailAddressMask
{
    public static string Mask(string address)
    {
        int at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return "***";
        }

        ReadOnlySpan<char> localPart = address.AsSpan(0, at);
        return string.Concat(localPart[..StringInfo.GetNextTextElementLength(localPart)], "***", address.AsSpan(at));
    }
}
