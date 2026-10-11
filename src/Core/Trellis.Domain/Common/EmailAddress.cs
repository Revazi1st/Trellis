using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

/// <summary>
/// Validated email address value object; construct via <see cref="Create"/>. Deliberately shallow:
/// real deliverability is proven by sending mail, not by a regex. The checks are the ones a mail system
/// would refuse outright — the length limits, one <c>@</c>, no whitespace, no empty or hyphen-edged
/// domain label, no leading, trailing or doubled dot — and any other character is allowed, so an
/// internationalized address (<c>dörte@exämple.de</c>) is accepted as typed.
/// </summary>
public sealed record EmailAddress
{
    /// <summary>RFC 5321 bounds a forward path at 256 octets including its angle brackets, which leaves 254 for the address.</summary>
    public const int MaxLength = 254;

    /// <summary>RFC 5321 §4.5.3.1.1: the part before the <c>@</c>.</summary>
    public const int MaxLocalPartLength = 64;

    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static Result<EmailAddress> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EmailAddressErrors.Empty;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            return EmailAddressErrors.TooLong;
        }

        int atIndex = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (atIndex <= 0
            || atIndex == trimmed.Length - 1
            || trimmed.IndexOf('@', atIndex + 1) >= 0
            || trimmed.Any(char.IsWhiteSpace))
        {
            return EmailAddressErrors.Invalid;
        }

        if (atIndex > MaxLocalPartLength)
        {
            return EmailAddressErrors.TooLong;
        }

        return IsLocalPart(trimmed.AsSpan(0, atIndex)) && IsDomain(trimmed.AsSpan(atIndex + 1))
            ? new EmailAddress(trimmed)
            : EmailAddressErrors.Invalid;
    }

    public override string ToString() => Value;

    /// <summary>No leading or trailing dot and no two dots in a row: <c>user..name</c> and <c>.user</c> are refused.</summary>
    private static bool IsLocalPart(ReadOnlySpan<char> localPart)
        => localPart[0] != '.' && localPart[^1] != '.' && !localPart.Contains("..", StringComparison.Ordinal);

    /// <summary>At least two labels, none empty (so no leading, trailing or doubled dot) and none starting or ending with a hyphen.</summary>
    private static bool IsDomain(ReadOnlySpan<char> domain)
    {
        int labels = 0;
        foreach (Range range in domain.Split('.'))
        {
            ReadOnlySpan<char> label = domain[range];
            if (label.IsEmpty || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            labels++;
        }

        return labels >= 2;
    }
}
