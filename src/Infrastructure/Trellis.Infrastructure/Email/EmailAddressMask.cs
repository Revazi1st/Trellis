namespace Trellis.Infrastructure.Email;

/// <summary>
/// The masked form of an e-mail address for logs: <c>jane.doe@example.com</c> → <c>j***@example.com</c>.
/// Enough to tell which domain a message went to, not enough to read the address back. The same shape
/// Persistence's security events use (ADR-0021); the two projects are peers, so each keeps its copy.
/// </summary>
internal static class EmailAddressMask
{
    public static string Mask(string address)
    {
        int at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1
            ? string.Concat(address.AsSpan(0, 1), "***", address.AsSpan(at))
            : "***";
    }
}
