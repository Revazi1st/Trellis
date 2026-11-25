using System.Globalization;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// The masked form of an e-mail address for logs: <c>jane.doe@example.com</c> → <c>j***@example.com</c>.
/// Enough to tell which domain a message went to, not enough to read the address back. The same shape
/// Persistence's security events use (ADR-0021); the two projects are peers, so each keeps its copy.
/// <para>
/// The local part keeps its first character as a reader sees it (a text element), never a lone UTF-16
/// code unit: a first character outside the Basic Multilingual Plane is two units, and half of the pair is
/// text UTF-8 cannot encode, so the outbox could not save the failure that carries it.
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
